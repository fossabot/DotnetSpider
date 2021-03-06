﻿using Cassandra;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using DotnetSpider.Extension.Model;
using DotnetSpider.Core.Infrastructure;
using DotnetSpider.Core;
using DotnetSpider.Extension.Infrastructure;
using DotnetSpider.Core.Redial;

namespace DotnetSpider.Extension.Pipeline
{
	/// <summary>
	/// 把解析到的爬虫实体数据存到Cassandra中
	/// </summary>
	public class CassandraEntityPipeline : BaseEntityPipeline
	{
		private PipelineMode _defaultPipelineModel;
		private static readonly TimeUuid DefaultTimeUuid = default(TimeUuid);
		private ISession _session;
		private CassandraConnectionSetting ConnectionSetting { get; set; }

		/// <summary>
		/// 构造方法
		/// </summary>
		public CassandraEntityPipeline() : this(Env.DataConnectionStringSettings?.ConnectionString)
		{
		}

		/// <summary>
		/// 构造方法
		/// </summary>
		/// <param name="connectString">连接字符串</param>
		public CassandraEntityPipeline(string connectString)
		{
			ConnectionSetting = new CassandraConnectionSetting(connectString);
		}

		/// <summary>
		/// 数据管道模式
		/// </summary>
		public PipelineMode DefaultPipelineModel
		{
			get => _defaultPipelineModel;
			set
			{
				if (value == PipelineMode.Update)
				{
					throw new SpiderException("Can not set pipeline mode to update");
				}
				if (!Equals(value, _defaultPipelineModel))
				{
					_defaultPipelineModel = value;
				}
			}
		}

		/// <summary>
		/// 添加爬虫实体类的定义
		/// </summary>
		/// <param name="entityDefine">爬虫实体类的定义</param>
		public override void AddEntity(IEntityDefine entityDefine)
		{
			if (entityDefine == null)
			{
				throw new ArgumentException("Should not add a null entity to a entity dabase pipeline.");
			}

			if (!typeof(CassandraSpiderEntity).IsAssignableFrom(entityDefine.Type))
			{
				throw new ArgumentException("Cassandra pipeline only support CassandraSpiderEntity.");
			}

			if (entityDefine.TableInfo == null)
			{
				Logger.Log($"Schema is necessary, Skip {GetType().Name} for {entityDefine.Name}.", Level.Warn);
				return;
			}

			EntityAdapter entityAdapter = new EntityAdapter(entityDefine.TableInfo, entityDefine.Columns);

			if (entityAdapter.Table.UpdateColumns != null && entityAdapter.Table.UpdateColumns.Length > 0)
			{
				entityAdapter.PipelineMode = PipelineMode.Update;
			}
			else
			{
				entityAdapter.PipelineMode = DefaultPipelineModel;
			}

			InitAllCqlOfEntity(entityAdapter);

			EntityAdapters.TryAdd(entityDefine.Name, entityAdapter);
		}
		
		/// <summary>
		/// 处理爬虫实体解析器解析到的实体数据结果
		/// </summary>
		/// <param name="name">爬虫实体类的名称</param>
		/// <param name="datas">实体类数据</param>
		/// <param name="spider">爬虫</param>
		/// <returns>最终影响结果数量(如数据库影响行数)</returns>
		public override int Process(string name, IEnumerable<dynamic> datas, ISpider spider)
		{
			if (datas == null)
			{
				return 0;
			}

			if (EntityAdapters.TryGetValue(name, out var metadata))
			{
				switch (metadata.PipelineMode)
				{
					default:
						{
							var action = new Action(() =>
							{
								var insertStmt = _session.Prepare(metadata.InsertSql);
								var batch = new BatchStatement();
								foreach (var data in datas)
								{
									List<object> values = new List<object>();
									foreach (var column in metadata.Columns)
									{
										if (column.DataType.FullName != DataTypeNames.TimeUuid)
										{
											values.Add(column.Property.GetValue(data));
										}
										else
										{
											var value = column.Property.GetValue(data);
											values.Add(Equals(value, DefaultTimeUuid) ? TimeUuid.NewId() : value);
										}
									}

									batch.Add(insertStmt.Bind(values.ToArray()));
								}
								// Execute the batch
								_session.Execute(batch);
							});
							if (DbExecutor.UseNetworkCenter)
							{
								NetworkCenter.Current.Execute("db", action);
							}
							else
							{
								action();
							}

							break;
						}
					case PipelineMode.InsertNewAndUpdateOld:
						{
							throw new NotImplementedException("Cassandra not suport InsertNewAndUpdateOld yet.");
						}
				}
			}
			return datas.Count();
		}
		
		/// <summary>
		/// 在使用数据管道前, 进行一些初始化工作, 不是所有的数据管道都需要进行初始化
		/// </summary>
		public override void Init()
		{
			base.Init();

			if (_session == null)
			{
				var cluster = CassandraUtil.CreateCluster(ConnectionSetting);
				_session = cluster.Connect();
			}

			foreach (var adapter in EntityAdapters)
			{
				_session.CreateKeyspaceIfNotExists(adapter.Value.Table.Database);
				_session.ChangeKeyspace(adapter.Value.Table.Database);
				_session.Execute(GenerateCreateTableSql(adapter.Value));
				var createIndexCql = GenerateCreateIndexes(adapter.Value);
				if (!string.IsNullOrWhiteSpace(createIndexCql))
				{
					_session.Execute(createIndexCql);
				}
			}
		}
		
		/// <summary>
		/// Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
		/// </summary>
		public override void Dispose()
		{
			base.Dispose();
			_session.Dispose();
			EntityAdapters.Clear();
		}

		private void InitAllCqlOfEntity(EntityAdapter adapter)
		{
			if (adapter.PipelineMode == PipelineMode.InsertNewAndUpdateOld)
			{
				//Logger.MyLog(Spider.Identity, "Cassandra only check if primary key duplicate.", NLog.LogLevel.Warn);
				throw new NotImplementedException("Cassandra not suport InsertNewAndUpdateOld yet.");
			}
			adapter.InsertSql = GenerateInsertSql(adapter);
			if (adapter.PipelineMode == PipelineMode.Update)
			{
				adapter.UpdateSql = GenerateUpdateSql();
			}
			adapter.SelectSql = GenerateSelectSql();
		}

		private string GenerateSelectSql()
		{
			return null;
		}

		private string GenerateUpdateSql()
		{
			return null;
		}

		private string GenerateInsertSql(EntityAdapter adapter)
		{
			var columNames = string.Join(", ", adapter.Columns.Select(p => $"{p.Name}"));
			var values = string.Join(", ", adapter.Columns.Select(column => $"?"));
			var tableName = adapter.Table.CalculateTableName();
			var sqlBuilder = new StringBuilder();

			sqlBuilder.AppendFormat("INSERT INTO {0} {1} {2};",
				tableName,
				string.IsNullOrWhiteSpace(columNames) ? string.Empty : $"({columNames})",
				string.IsNullOrEmpty(values) ? string.Empty : $" VALUES ({values})");

			var sql = sqlBuilder.ToString();
			return sql;
		}

		private string GenerateCreateTableSql(EntityAdapter adapter)
		{
			var tableName = adapter.Table.CalculateTableName();

			StringBuilder builder = new StringBuilder($"CREATE TABLE IF NOT EXISTS {adapter.Table.Database }.{tableName} (");
			string columNames = string.Join(", ", adapter.Columns.Select(p => $"{p.Name} {GetDataTypeSql(p)} "));
			builder.Append(columNames);
			builder.Append($", PRIMARY KEY(Id)");

			builder.Append(")");
			string sql = builder.ToString();
			return sql;
		}

		private string GenerateCreateIndexes(EntityAdapter adapter)
		{
			StringBuilder builder = new StringBuilder();
			if (adapter.Table.Indexs != null)
			{
				foreach (var index in adapter.Table.Indexs)
				{
					var columns = index.Split(',');
					string name = string.Join("_", columns.Select(c => c));
					string indexColumNames = string.Join(", ", columns.Select(c => $"{c}"));

					builder.Append($"CREATE INDEX IF NOT EXISTS {name} ON {adapter.Table.Database}.{adapter.Table.CalculateTableName()}({indexColumNames});");
				}
			}
			if (adapter.Table.Uniques != null)
			{
				throw new SpiderException("Cassandra not support unique index");
			}

			var sql = builder.ToString();
			return sql;
		}

		private string GetDataTypeSql(Column field)
		{
			var dataType = "text";

			if (field.DataType.FullName == DataTypeNames.Boolean)
			{
				dataType = "boolean";
			}
			else if (field.DataType.FullName == DataTypeNames.DateTime)
			{
				dataType = "timestamp";
			}
			else if (field.DataType.FullName == DataTypeNames.Decimal)
			{
				dataType = "decimal";
			}
			else if (field.DataType.FullName == DataTypeNames.Double)
			{
				dataType = "double";
			}
			else if (field.DataType.FullName == DataTypeNames.Float)
			{
				dataType = "float";
			}
			else if (field.DataType.FullName == DataTypeNames.Int)
			{
				dataType = "int";
			}
			else if (field.DataType.FullName == DataTypeNames.Int64)
			{
				dataType = "bigint";
			}
			else if (field.DataType.FullName == DataTypeNames.String)
			{
				dataType = "text";
			}
			else if (field.DataType.FullName == DataTypeNames.TimeUuid)
			{
				dataType = "uuid";
			}

			return dataType;
		}
	}
}
