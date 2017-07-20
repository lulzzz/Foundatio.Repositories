﻿using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Foundatio.Repositories.Marten.Tests.Repositories.Models;
using Foundatio.Repositories.Marten.Queries.Builders;
using Foundatio.Repositories.Options;

namespace Foundatio.Repositories {
    public static class AgeQueryExtensions {
        internal const string AgesKey = "@Ages";

        public static T Age<T>(this T query, int age) where T : IRepositoryQuery {
            return query.AddCollectionOptionValue(AgesKey, age);
        }

        public static T AgeRange<T>(this T query, int minAge, int maxAge) where T : IRepositoryQuery {
            foreach (var age in Enumerable.Range(minAge, maxAge - minAge + 1))
                query.AddCollectionOptionValue(AgesKey, age);

            return query;
        }
    }
}

namespace Foundatio.Repositories.Options {
    public static class ReadAgeQueryExtensions {
        public static ICollection<int> GetAges(this IRepositoryQuery query) {
            return query.SafeGetCollection<int>(AgeQueryExtensions.AgesKey);
        }
    }
}

namespace Foundatio.Repositories.Marten.Tests.Repositories.Queries {
    public class AgeQueryBuilder : IMartenQueryBuilder {
        public Task BuildAsync<T>(QueryBuilderContext<T> ctx) where T : class, new() {
            var ages = ctx.Source.GetAges();
            if (ages.Count <= 0)
                return Task.CompletedTask;

            if (ages.Count == 1)
                ctx.QueryAnd<Employee>(e => e.Age == ages.First());
            else
                ctx.QueryAnd<Employee>(e => ages.Contains(e.Age));

            return Task.CompletedTask;
        }
    }
}