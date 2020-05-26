﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Exceptionless.Core.Extensions;
using Exceptionless.Core.Models;
using Exceptionless.Core.Repositories.Configuration;
using Exceptionless.Core.Repositories.Queries;
using FluentValidation;
using Foundatio.Repositories;
using Foundatio.Repositories.Models;
using Foundatio.Utility;
using Nest;

namespace Exceptionless.Core.Repositories {
    public class ProjectRepository : RepositoryOwnedByOrganization<Project>, IProjectRepository {
        public ProjectRepository(ExceptionlessElasticConfiguration configuration, IValidator<Project> validator, AppOptions options)
            : base(configuration.Projects, validator, options) {
        }

        public Task<CountResult> GetCountByOrganizationIdAsync(string organizationId) {
            if (String.IsNullOrEmpty(organizationId))
                throw new ArgumentNullException(nameof(organizationId));

            return CountByQueryAsync(q => q.Organization(organizationId), o => o.CacheKey(String.Concat("Organization:", organizationId)));
        }

        public Task<QueryResults<Project>> GetByOrganizationIdsAsync(ICollection<string> organizationIds, CommandOptionsDescriptor<Project> options = null) {
            if (organizationIds == null)
                throw new ArgumentNullException(nameof(organizationIds));

            if (organizationIds.Count == 0)
                return Task.FromResult(new QueryResults<Project>());
            
            return QueryAsync(q => q.Organization(organizationIds).SortAscending(p => p.Name.Suffix("keyword")), options);
        }
        
        public Task<QueryResults<Project>> GetByFilterAsync(AppFilter systemFilter, string userFilter, string sort, CommandOptionsDescriptor<Project> options = null) {
            IRepositoryQuery<Project> query = new RepositoryQuery<Project>()
                .AppFilter(systemFilter)
                .FilterExpression(userFilter);

            query = !String.IsNullOrEmpty(sort) ? query.SortExpression(sort) : query.SortAscending(p => p.Name.Suffix("keyword"));
            return QueryAsync(q => query, options);
        }

        public Task<QueryResults<Project>> GetByNextSummaryNotificationOffsetAsync(byte hourToSendNotificationsAfterUtcMidnight, int limit = 50) {
            var filter = Query<Project>.Range(r => r.Field(o => o.NextSummaryEndOfDayTicks).LessThan(SystemClock.UtcNow.Ticks - (TimeSpan.TicksPerHour * hourToSendNotificationsAfterUtcMidnight)));
            return QueryAsync(q => q.ElasticFilter(filter).SortAscending(p => p.OrganizationId), o => o.SnapshotPaging().PageLimit(limit));
        }

        public async Task IncrementNextSummaryEndOfDayTicksAsync(IReadOnlyCollection<Project> projects) {
            if (projects == null)
                throw new ArgumentNullException(nameof(projects));

            if (projects.Count == 0)
                return;

            string script = $"ctx._source.next_summary_end_of_day_ticks += {TimeSpan.TicksPerDay}L;";
            await this.PatchAsync(projects.Select(p => p.Id).ToArray(), new ScriptPatch(script), o => o.Notifications(false)).AnyContext();
            await InvalidateCacheAsync(projects).AnyContext();
        }

        protected override Task InvalidateCachedQueriesAsync(IReadOnlyCollection<Project> documents, ICommandOptions options = null) {
            var organizations = documents.Select(d => d.OrganizationId).Distinct().Where(id => !String.IsNullOrEmpty(id));
            return Task.WhenAll(Cache.RemoveAllAsync(organizations.Select(id => $"count:Organization:{id}")), base.InvalidateCachedQueriesAsync(documents, options));
        }
    }
}
