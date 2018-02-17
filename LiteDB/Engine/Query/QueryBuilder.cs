﻿using System;
using System.Collections.Generic;
using System.Linq;

namespace LiteDB
{
    /// <summary>
    /// Class to provider a fluent query API to complex queries. This class will be optimied to convert into Query class before run
    /// </summary>
    public class QueryBuilder
    {
        private string _collection;
        private LiteTransaction _transaction;
        private LiteEngine _engine;



        private QueryPlan _query = new QueryPlan();
        private List<BsonExpression> _where = new List<BsonExpression>();

        public QueryBuilder()
        {
        }

        public QueryBuilder(string collection, LiteTransaction transaction, LiteEngine engine)
        {
            _collection = collection;
            _transaction = transaction;
            _engine = engine;
        }

        #region FluentApi

        /// <summary>
        /// Add new WHERE statement in your query. Can be executed with an index or via full scan
        /// </summary>
        public QueryBuilder Where(BsonExpression predicate)
        {
            // add expression in where list breaking AND statments
            if (predicate.IsConditional || predicate.Type == BsonExpressionType.Or)
            {
                _where.Add(predicate);
            }
            else if(predicate.Type == BsonExpressionType.And)
            {
                this.Where(predicate.Left);
                this.Where(predicate.Right);
            }
            else
            {
                throw LiteException.InvalidExpressionTypeConditional(predicate);
            }

            return this;
        }

        /// <summary>
        /// Add new WHERE statement in your query. Can be executed with an index or via full scan
        /// </summary>
        public QueryBuilder Where(string predicate, params BsonValue[] args)
        {
            return this.Where(BsonExpression.Create(predicate, args));
        }

        /// <summary>
        /// Add new WHERE statement in your query. Can be executed with an index or via full scan
        /// </summary>
        public QueryBuilder Where(string predicate, BsonDocument parameters)
        {
            return this.Where(BsonExpression.Create(predicate, parameters));
        }

        /// <summary>
        /// Load cross reference documents from path expression (DbRef reference). Call this method before Where() if you want use this reference in your filter (slow).
        /// </summary>
        public QueryBuilder Include(string include)
        {
            var path = BsonExpression.Create(include);

            if (path.Type == BsonExpressionType.Path) throw LiteException.InvalidExpressionType(path, BsonExpressionType.Path);

            var list = _where.Count == 0 ? _query.IncludeBefore : _query.IncludeAfter;

            list.Add(path);

            return this;
        }

        /// <summary>
        /// Add order by on your result. OrderBy paramter can be an expression
        /// </summary>
        public QueryBuilder OrderBy(string orderBy, int order = Query.Ascending)
        {
            _query.OrderBy = BsonExpression.Create(orderBy);
            _query.Order = order;

            return this;
        }

        public QueryBuilder GroupBy(string groupBy)
        {
            return this;
        }

        /// <summary>
        /// Limit your resultset
        /// </summary>
        public QueryBuilder Limit(int limit)
        {
            _query.Limit = limit;

            return this;
        }

        /// <summary>
        /// Skip/offset your resultset
        /// </summary>
        public QueryBuilder Offset(int offset)
        {
            _query.Offset = offset;

            return this;
        }

        /// <summary>
        /// Transform your output document using this select expression
        /// </summary>
        public QueryBuilder Select(string select)
        {
            _query.Select = BsonExpression.Create(select);

            return this;
        }

        /// <summary>
        /// Execute query locking collection in write mode. This is avoid any other thread change results after read document and before transaction ends.
        /// </summary>
        public QueryBuilder ForUpdate()
        {
            _query.ForUpdate = true;

            return this;
        }

        /// <summary>
        /// Define your own index conditional expression to run over collection. 
        /// If not defined (default), QueryAnalyzer will be auto select best option or create a new one.
        /// Use this option only if you want define index and do not use QueryAnalyzer.
        /// </summary>
        public QueryBuilder Index(Index index)
        {
            _query.Index = index;

            return this;
        }

        #endregion

        #region Internal Execute

        /// <summary>
        /// Find for documents in a collection using Query definition
        /// </summary>
        internal IEnumerable<BsonDocument> Run(bool countOnly)
        {
            // call DoFind inside snapshot
            return _transaction.CreateSnapshot(_query.ForUpdate ? SnapshotMode.Write : SnapshotMode.Read, _collection, false, snapshot =>
            {
                // execute query analyze before run query (will change _query instance)
                var analyzer = new QueryAnalyzer(snapshot, _query, _where, countOnly, true);

                analyzer.RunAnalyzer();

                return DoFind(snapshot);
            });

            // executing query
            IEnumerable<BsonDocument> DoFind(Snapshot snapshot)
            {
                var col = snapshot.CollectionPage;
                var data = new DataService(snapshot);
                var indexer = new IndexService(snapshot);
                var loader = new DocumentLoader(data, _engine.BsonReader);

                // no collection, no documents
                if (col == null) yield break;

                // get node list from query
                var nodes = _query.Index.Run(col, indexer);

                // load document from disk
                var docs = LoadDocument(nodes, loader, _query.KeyOnly, _query.Index.Name);

                // load pipe query to apply all query options
                var pipe = new QueryPipe(_engine, _transaction, loader);

                // call safepoint just before return each document
                foreach (var doc in pipe.Pipe(docs, _query))
                {
                    _transaction.Safepoint();

                    yield return doc;
                }
            }

            // load documents from disk or make a "fake" document using key only (useful for COUNT/EXISTS)
            IEnumerable<BsonDocument> LoadDocument(IEnumerable<IndexNode> nodes, IDocumentLoader loader, bool keyOnly, string name)
            {
                foreach (var node in nodes)
                {
                    yield return keyOnly ?
                        new BsonDocument { [name] = node.Key } :
                        loader.Load(node.DataBlock);
                }
            }
        }

        #endregion

        #region Query Shortcut

        /// <summary>
        /// Execute query and return documents as IEnumerable
        /// </summary>
        public List<BsonDocument> ToEnumerable()
        {
            return this.Run(false).ToList();
        }

        /// <summary>
        /// Execute query and return as List of BsonDocument
        /// </summary>
        public List<BsonDocument> ToList()
        {
            return this.Run(false).ToList();
        }

        /// <summary>
        /// Execute query and return as array of BsonDocument
        /// </summary>
        public BsonDocument[] ToArray()
        {
            return this.Run(false).ToArray();
        }

        /// <summary>
        /// Execute Single over ToEnumerable result documents
        /// </summary>
        public BsonDocument SingleById(BsonValue id)
        {
            return this
                .Index(LiteDB.Index.EQ("_id", id))
                .Single();
        }

        /// <summary>
        /// Execute Single over ToEnumerable result documents
        /// </summary>
        public BsonDocument Single()
        {
            return this.Run(false).Single();
        }

        /// <summary>
        /// Execute SingleOrDefault over ToEnumerable result documents
        /// </summary>
        public BsonDocument SingleOrDefault()
        {
            return this.Run(false).SingleOrDefault();
        }

        /// <summary>
        /// Execute First over ToEnumerable result documents
        /// </summary>
        public BsonDocument First()
        {
            return this.Run(false).First();
        }

        /// <summary>
        /// Execute FirstOrDefault over ToEnumerable result documents
        /// </summary>
        public BsonDocument FirstOrDefault()
        {
            return this.Run(false).FirstOrDefault();
        }

        /// <summary>
        /// Execute count over document resultset
        /// </summary>
        public int Count()
        {
            return this.Run(true).Count();
        }

        /// <summary>
        /// Execute exists over document resultset
        /// </summary>
        public bool Exists()
        {
            return this.Run(true).Any();
        }

        #endregion
    }
}