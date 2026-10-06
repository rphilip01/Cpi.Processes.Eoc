using Azure.Messaging.ServiceBus;
using Cpi.Preprocessor.Core.Lead;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using static Cpi.Processes.Eoc.EocProcessing;

namespace Cpi.Processes.Eoc
{
    public class EocProcessing
    {
        private readonly ILogger<EocProcessing> _logger;
        private readonly IOptions<MessagingOptions> _messagingOptions;
        private readonly ServiceBusSender _serviceBusSender;
        private readonly SenderServiceBusClient _senderClient;
        private readonly IConfiguration _configuration;
        private readonly IOrganizationServiceAsync2 _serviceClient;
        private readonly ICrmClient _crmClient;


        public EocProcessing(ICrmClient crmClient, IOrganizationServiceAsync2 orgSvc, ILogger<EocProcessing> logger, IOptions<MessagingOptions> messagingOptions, ServiceBusSender sender, SenderServiceBusClient senderClient, IConfiguration configuration)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _messagingOptions = messagingOptions;
            _serviceBusSender = sender;
            _senderClient = senderClient;
            _configuration = configuration;
            _serviceClient = orgSvc;
            _crmClient = crmClient;

            // Test that logger works immediately
            Console.WriteLine("EocProcessing constructor called - Logger injected successfully");
            _logger.LogInformation("EocProcessing instance created with logger");
        }





            //public EocProcessing(ICrmClient crmClient, ILogger<EocProcessing> logger)
            //{
            //    _crmClient = crmClient;
            //    _logger = logger;
            //}
        public static class EocProcessingConstants
        {
            public const string CncyProcessed = "cncy_processed";
            public const string CncyEnded = "cncy_ended";
            public const string CpiReprocess = "cpi_reprocess";
        }


        #region Message Options Class
        public record MessagingOptions
        {
            public const string Messaging = "Messaging";
            public string? RetryCountProperty { get; set; }
            public string? SequenceProperty { get; set; }
            public int RetryCount { get; set; }
            public int BaseMinutes { get; set; }
        }
        #endregion


        [Function("DailyEocProcessing")]
        public void DailyEocProcessing([TimerTrigger("%DailyEocTimer%")] TimerInfo myTimer)
        {
            try
            {

                EntityCollection startCandidates = _crmClient.RetrieveEocsThatHaveReachedStartDate();
                EntityCollection endCandidates = _crmClient.RetrieveEocsThatHaveReachedEndDate();

                var allCandidates = new List<Entity>();

                if (startCandidates?.Entities != null)
                    allCandidates.AddRange(startCandidates.Entities);
                if (endCandidates?.Entities != null)
                    allCandidates.AddRange(endCandidates.Entities);

                foreach (var eoc in allCandidates)
                {
                    try
                    {
                        var update = new Entity("cncy_endofcertification", eoc.Id);
                        if (eoc.Contains(EocProcessingConstants.CpiReprocess))
                        {
                            var currentValue = (bool)eoc[EocProcessingConstants.CpiReprocess];
                            update[EocProcessingConstants.CpiReprocess] = !currentValue;
                        }
                        else
                        {
                            update[EocProcessingConstants.CpiReprocess] = true;
                        }

                        _crmClient.Update(update);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to set cpi_reprocess for EOC {Id}", eoc.Id);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process Daily EOC batch.");
            }
        }

        [Function("ImmediateEocProcessing")]
        //public void ImmediateEocProcessing([ServiceBusTrigger("eoc-processing", Connection = "EocServiceBusConnectionString")] string queueItem)
        public void ImmediateEocProcessing([ServiceBusTrigger("%eoc_processing_queue_name%", Connection = "eoc_processing:ServiceBus")] ServiceBusReceivedMessage message, ServiceBusMessageActions messageActions)
        {
            string eocID = RetrieveEOCIDFromServiceBusMessage(message.Body.ToString());
            Guid eocGuid = new Guid(eocID);

            Entity eocToProcess = _crmClient.Retrieve("cncy_endofcertification", eocGuid, new ColumnSet(
                "cncy_businessadministrator", "cncy_contact", "cncy_enddate", "cncy_ended",
                "cncy_name", "cncy_processed", "cncy_startdate", "statecode",
                "cncy_stoppedtrainingdetail", "cncy_stoppedtrainingreason", "cpi_reprocess"));

            if (eocToProcess == null)
            {
                _logger.LogWarning("EOC {id} not found. Skipping.", eocID);
                return;
            }

            EntityCollection recordsToProcess = new();
            recordsToProcess.Entities.Add(eocToProcess);
            DateTime? endDate = eocToProcess.GetAttributeValue<DateTime?>("cncy_enddate");
            try
            {
                if (!eocToProcess.GetAttributeValue<bool>("cncy_processed"))
                {
                    ProcessEocStart(recordsToProcess, true);
                }
                else if (!eocToProcess.GetAttributeValue<bool>("cncy_ended") && endDate.HasValue && endDate.Value.Date <= DateTime.UtcNow.Date)
                {
                    ProcessEocEnd(recordsToProcess);
                }
                var clear = new Entity("cncy_endofcertification", eocGuid);
                clear["cpi_reprocess"] = false;
                _crmClient.Update(clear);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ImmediateEocProcessing failed for EOC {id}", eocID);

            }
        }

        private void ProcessEocStart(EntityCollection records, bool immediateProcessing)
        {
            Entity currentContact = null;
            foreach (Entity eoc in records.Entities)
            {
                try
                {
                    string stoppedTrainingCode = _crmClient.GetStoppedTrainingCode(eoc);
                    bool createEmploymentHistory = !immediateProcessing && IsBoeChange(stoppedTrainingCode);
                    Entity eocContact = null;
                    if (createEmploymentHistory)
                        eocContact = _crmClient.RetrieveEocContact(eoc, true);
                    else
                        eocContact = _crmClient.RetrieveEocContact(eoc, false);
                    currentContact = eocContact;
                    eocContact = UpdateContactBAStatus(eoc, eocContact, stoppedTrainingCode);
                    eocContact = UpdateContactAndCertYearsToInactive(eocContact);
                    eocContact = _crmClient.UpdateCommunicationDetails(eocContact, true);
                    eocContact = _crmClient.UpdateExcludeFromDocumentationDetails(eocContact, !eocContact.GetAttributeValue<bool>("cpi_businessadministrator"));
                    _crmClient.Update(eocContact);
                    if (createEmploymentHistory)
                    {
                        _crmClient.CreateEmploymentHistoryRecord(eocContact);
                    }
                    Entity currentEoc = eoc;
                    currentEoc = _crmClient.UpdateEocStatus(currentEoc, true, false);
                    _crmClient.Update(currentEoc);
                }
                catch (Exception ex)
                {
                    _logger.LogError(1, ex, ex.Message + "; Error attempting to process EOC Start Date for EOC: " + eoc.Id.ToString() + " belonging to Contact: " + currentContact.Id.ToString());
                }
            }
        }

        private void ProcessEocEnd(EntityCollection records)
        {
            Entity eocContact = null;
            foreach (Entity eoc in records.Entities)
            {
                try
                {
                    eocContact = _crmClient.RetrieveEocContact(eoc, false);
                    Entity employmentHistory = _crmClient.RetrieveEmploymentHistory(eocContact);
                    if (employmentHistory != null)
                    {
                        eocContact = _crmClient.RevertCommunicationUpdates(eocContact, employmentHistory);
                    }
                    ReactivateEligibleCertYears(eocContact);
                    eocContact = _crmClient.UpdateExcludeFromDocumentationDetails(eocContact, false);

                    _crmClient.Update(eocContact);

                    Entity currentEoc = eoc;
                    currentEoc = _crmClient.UpdateEocStatus(currentEoc, true, true);
                    _crmClient.Update(currentEoc);
                }
                catch (Exception ex)
                {
                    _logger.LogError(1, ex, ex.Message + "; Error attempting to process EOC End Date for EOC: " + eoc.Id.ToString() + " belonging to Contact: " + eocContact.Id.ToString());
                }
            }
        }


        private void ReactivateEligibleCertYears(Entity contact)
        {
            EntityCollection certYears;

            try
            {
                certYears =
                    _crmClient.GetInactiveCertYearsWithFutureEndDateForContact(contact.Id);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Failed to retrieve inactive future-dated cert years for Contact {ContactId}",
                    contact.Id);
                return;
            }

            if (certYears?.Entities == null || certYears.Entities.Count == 0)
            {
                return;
            }

            if (!int.TryParse(
                Environment.GetEnvironmentVariable("CertYearActiveStatus"),
                out int activeStatusValue))
            {
                _logger.LogError(
                    "CertYearActiveStatus is missing or invalid. Cannot reactivate cert years.");
                return;
            }

            foreach (Entity certYear in certYears.Entities)
            {
                try
                {
                    certYear["cncy_status"] = new OptionSetValue(activeStatusValue);
                    certYear["cpi_inactivereason"] = null;

                    _crmClient.Update(certYear);
                }
                catch (Exception ex)
                {
                    _logger.LogError(
                        ex,
                        "Failed to reactivate cert year {CertYearId}",
                        certYear.Id);
                }
            }
        }

        private static string RetrieveEOCIDFromServiceBusMessage(string queueItem)
        {
            dynamic message = JsonConvert.DeserializeObject<dynamic>(queueItem);
            string eocID = message.PrimaryEntityId.Value.ToString();

            return eocID;
        }

        private static Entity UpdateContactBAStatus(Entity eoc, Entity contact, string stoppedTrainingCode)
        {
            if (eoc.GetAttributeValue<bool>("cncy_businessadministrator"))
            {
                string[] baStatusArray = Environment.GetEnvironmentVariable("BAUpdateStatuses").ToString().Split(',');
                List<string> baStatuses = new(baStatusArray);
                if (baStatuses.Contains(stoppedTrainingCode))
                {
                    contact.Attributes["cpi_businessadministrator"] = eoc.GetAttributeValue<bool>("cncy_businessadministrator");
                }
            }
            else
            {
                contact.Attributes["cpi_businessadministrator"] = eoc.GetAttributeValue<bool>("cncy_businessadministrator");
            }

            return contact;
        }

        private Entity UpdateContactAndCertYearsToInactive(Entity contact)
        {
            contact.Attributes["cncy_cistatus"] = "Inactive";

            EntityCollection certYearSummaries = _crmClient.GetCertYearSummariesToInactivateForContact(contact.Id);
            EntityCollection pendingYears = _crmClient.GetPendingYearsForContact(contact.Id);

            ProcessCertYears(certYearSummaries, false);
            ProcessCertYears(pendingYears, true);

            return contact;
        }

        private void ProcessCertYears(EntityCollection certYearSummaries, bool pendingYears)
        {
            Entity currentCertYear = null;
            OptionSetValue inactive = new(int.Parse(Environment.GetEnvironmentVariable("CertYearInactiveStatus")));
            OptionSetValue inactiveReason = new(int.Parse(Environment.GetEnvironmentVariable("CertYearInactiveReason")));



            foreach (Entity certYear in certYearSummaries.Entities)
            {
                currentCertYear = certYear;
                if (!pendingYears)
                {
                    currentCertYear.Attributes["cncy_status"] = inactive;
                    currentCertYear.Attributes["cpi_inactivereason"] = inactiveReason; // set inactive reason to EOC
                }

                _crmClient.Update(currentCertYear);
            }
        }

        private static bool IsBoeChange(string stoppedTrainingCode)
        {
            bool result = false;
            string[] employmentHistoryStatusArray = Environment.GetEnvironmentVariable("EmploymentHistoryCreateStatuses").ToString().Split(',');
            List<string> employmentHistoryStatuses = new(employmentHistoryStatusArray);
            if (employmentHistoryStatuses.Contains(stoppedTrainingCode))
            {
                result = true;
            }
            return result;
        }
    }
}