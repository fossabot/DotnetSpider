using System;
using System.Collections.Generic;
using DotnetSpider.Extension.Model;
using Newtonsoft.Json.Linq;
using DotnetSpider.Core.Infrastructure;

namespace DotnetSpider.Extension.Pipeline
{
	/// <summary>
	/// Print page model in console
	/// Usually used in test.
	/// </summary>
	public class ConsoleEntityPipeline : BaseEntityPipeline
	{
		public override void InitEntity(EntityMetadata metadata)
		{
			if (metadata.Schema == null)
			{
				Spider.Log($"Schema is necessary, Pass {GetType().Name} for {metadata.Entity.Name}.", LogLevel.Warn);
				return;
			}
			IsEnabled = true;
		}

		public override void Process(List<JObject> datas)
		{
			foreach (var data in datas)
			{
				Console.WriteLine(data.ToString());
			}
		}

		public override BaseEntityPipeline Clone()
		{
			return new ConsoleEntityPipeline();
		}
	}
}
