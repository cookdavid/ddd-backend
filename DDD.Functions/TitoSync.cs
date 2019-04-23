using DDD.Core.Tito;
using DDD.Functions.Extensions;
using Microsoft.Azure.WebJobs;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Formatting;
using System.Net.Http.Headers;
using System.Threading.Tasks;

namespace DDD.Functions
{
    public static class TitoSync
    {
        private static TitoSyncConfig _titoSyncConfig;
        private static HttpClient _httpClient;
        private static ILogger _logger;

        [FunctionName("TitoSync")]
        public static async Task Run(
            [TimerTrigger("%TitoSyncSchedule%")]
            TimerInfo timer,
            ILogger logger,
            [BindConferenceConfig] ConferenceConfig conference,
            [BindKeyDatesConfig] KeyDatesConfig keyDates,
            [BindTitoSyncConfig] TitoSyncConfig titoSyncConfig
        )
        {
            _titoSyncConfig = titoSyncConfig;
            _logger = logger;

            if (keyDates.After(x => x.StopSyncingTitoFromDate, TimeSpan.FromMinutes(10)))
            {
                _logger.LogInformation("Tito sync sync date passed");
                return;
            }

            var ids = new List<string>();
            _httpClient = new HttpClient();
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Token", $"token={titoSyncConfig.ApiKey}");

            var (tickets, hasMoreItems, nextPage) = await GetRegistrationsAsync();

            if (tickets != null && tickets.Any())
            {
                ids.AddRange(tickets.Select(o => o.Id));
                _logger.LogInformation("Retrieved {registrationsCount} tickets from Tito.", tickets.Count());
            }

            while (hasMoreItems)
            {
                (tickets, hasMoreItems, nextPage) = await GetRegistrationsAsync(nextPage.Value);

                if (tickets != null && tickets.Any())
                {
                    _logger.LogInformation("Found more {registrationsCount} tickets from Tito.", tickets.Count());
                    ids.AddRange(tickets.Select(o => o.Id));
                }
            }

            var repo = await titoSyncConfig.GetRepositoryAsync();
            var existingTickets = await repo.GetAllAsync(conference.ConferenceInstance);

            // Taking up to 100 records to meet Azure Storage Bulk Operation limit
            var newTickets = ids.Except(existingTickets.Select(x => x.TicketId).ToArray()).Distinct().Take(100).ToArray();
            _logger.LogInformation(
                "Found {existingCount} existing tickets and {currentCount} current tickets. Inserting {newCount} new tickets.",
                existingTickets.Count, ids.Count, newTickets.Count());
            await repo.CreateBatchAsync(newTickets.Select(o => new TitoTicket(conference.ConferenceInstance, o))
                .ToArray());
        }


        private static async Task<(Ticket[], bool, int?)> GetRegistrationsAsync(int pageNumber = 1)
        {
            var titoUrl = $"https://api.tito.io/v3/{_titoSyncConfig.AccountId}/{_titoSyncConfig.EventId}/registrations?page={pageNumber}";
            var response = await _httpClient.GetAsync(titoUrl);

            if (response.IsSuccessStatusCode)
            {
                try
                {
                    var formatters = new MediaTypeFormatterCollection();
                    formatters.JsonFormatter.SupportedMediaTypes.Add(new MediaTypeHeaderValue("application/vnd.api+json"));
                    var content = await response.Content.ReadAsAsync<PaginatedTitoTicketsResponse>(formatters);

                    return (content.Tickets, content.Meta.HasMoreItems, content.Meta.NextPage);
                }
                catch (Exception ex)
                {
                    _logger.LogCritical("Error fomratting/reading Tito response. ", ex);
                }
            }
            else
            {
                _logger.LogCritical("Error connecting to Tito with http response: {reason}. The dump of the response: ", response.StatusCode, response.Content.ReadAsStringAsync());
            }
            return (null, false, null);
        }
    }

    public class PaginatedTitoTicketsResponse
    {
        [JsonProperty("meta")]
        public Meta Meta { get; set; }

        [JsonProperty("tickets")]
        public Ticket[] Tickets { get; set; }
    }

    public class Ticket
    {
        [JsonProperty("id")]
        public string Id { get; set; }
    }

    public class Meta
    {
        [JsonProperty("current_page")]
        public int CurrentPage { get; set; }

        [JsonProperty("next_page")]
        public int? NextPage { get; set; }

        [JsonProperty("total_pages")]
        public int TotalPages { get; set; }

        [JsonIgnore] public bool HasMoreItems => NextPage.HasValue || CurrentPage < TotalPages;
    }
}