using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Microsoft.PowerPlatform.Dataverse.Client;
using System;

namespace Cpi.Processes.Eoc
{
    public interface ICrmClient
    {
        string GetFormattedValue(Entity record, string fieldName);
        string GetStoppedTrainingCode(Entity eoc);
        Entity RetrieveEocContact(Entity eoc,bool returnAddressFields);
        EntityCollection RetrieveEocsThatHaveReachedStartDate();
        EntityCollection RetrieveEocsThatHaveReachedEndDate();
        EntityCollection GetCertYearSummariesToInactivateForContact(Guid contactID);
        EntityCollection GetPendingYearsForContact(Guid contactID);
        EntityCollection GetInactiveCertYearsWithFutureEndDateForContact(Guid contactID);
        Entity RetrieveEmploymentHistory(Entity contact);
        void CreateEmploymentHistoryRecord(Entity contact);
        Entity PopulateAttributeWithNullCheck(Entity entityToPopulate, string attributeToPopulate, Entity entityToPopulateFrom, string attributeToPopulateFrom);
        Entity UpdateCommunicationDetails(Entity contact, bool doNotAllow);
        Entity RevertCommunicationUpdates(Entity contact, Entity employmentHistory);
        Entity UpdateEocStatus(Entity eoc, bool processed, bool ended);
        Entity UpdateExcludeFromDocumentationDetails(Entity contact, bool exclude);
        Entity Retrieve(string entityName, Guid id, ColumnSet columns);
        void Update(Entity entity);
    }

    public class CrmClient : ICrmClient
    {
        private readonly IOrganizationServiceAsync2 _service;

        public CrmClient(IOrganizationServiceAsync2 service)
        {
            _service = service;
        }

        public Entity Retrieve(string entityName, Guid id, ColumnSet columns)
        {
            return _service.Retrieve(entityName, id, columns);
        }

        public void Update(Entity entity)
        {
            _service.Update(entity);
        }

        public string GetFormattedValue(Entity record, string fieldName)
        {
            if (record.Contains(fieldName))
                return record.FormattedValues[fieldName];

            return null;
        }

        public string GetStoppedTrainingCode(Entity eoc)
        {
            string stoppedTrainingReason = GetFormattedValue(eoc, "cncy_stoppedtrainingreason");
            string result;
            if (stoppedTrainingReason == "Left current organization or moved to a different organization")
            {
                result = stoppedTrainingReason;
            }
            else
            {
                result = GetFormattedValue(eoc, "cncy_stoppedtrainingdetail");
            }

            return result;
        }

        public Entity RetrieveEocContact(Entity eoc, bool returnAddressFields)
        {
            if (eoc.Contains("cncy_contact"))
            {
                EntityReference refContact = (EntityReference)eoc.Attributes["cncy_contact"];
                ColumnSet columns = new ColumnSet();
                columns = returnAddressFields ? new ColumnSet("cpi_customernumber", "cpi_businessadministrator", "cncy_cistatus", "parentcustomerid",
                    "telephone1", "cpi_address1type", "address1_line1", "address1_city", "cpi_address1state", "cpi_address1country", "address1_postalcode",
                    "emailaddress1", "donotemail", "donotfax", "donotphone", "donotpostalmail", "firstname", "lastname", "jobtitle", "cpi_territorycode",
                    "cpi_markettype", "cncy_excludefromdocumentation", "cncy_excludefromdocumentationdate") :
                    new ColumnSet("cpi_customernumber", "cpi_businessadministrator", "cncy_cistatus", "parentcustomerid",
                    "telephone1", "emailaddress1", "donotemail", "donotfax", "donotphone", "donotpostalmail", "firstname", "lastname", "jobtitle", "cpi_territorycode",
                    "cpi_markettype", "cncy_excludefromdocumentation", "cncy_excludefromdocumentationdate");

                return _service.Retrieve("contact", refContact.Id, columns);
            }
            return null;
        }


        public EntityCollection RetrieveEocsThatHaveReachedStartDate()
        {
            string fetchXml = String.Format(
                @"<fetch version='1.0' output-format='xml-platform' mapping='logical' distinct='false'>
                    <entity name='cncy_endofcertification'>
                    <attribute name='cncy_endofcertificationid' />
                    <attribute name='cncy_businessadministrator' />
                    <attribute name='cncy_contact' />
                    <attribute name='cncy_enddate' />
                    <attribute name='cncy_ended' />
                    <attribute name='cncy_name' />
                    <attribute name='cncy_processed' />
                    <attribute name='cncy_startdate' />
                    <attribute name='statecode' />
                    <attribute name='cncy_stoppedtrainingdetail' />
                    <attribute name='cncy_stoppedtrainingreason' />
                    <order attribute='cncy_startdate' descending='true' />
                    <filter type='and'>
                        <condition attribute='cncy_startdate' operator='on-or-before' value='{0}' />
                        <condition attribute='cncy_enddate' operator='null' />
                        <condition attribute='cncy_processed' operator='eq' value='0' />
                    </filter>
                    </entity>
                </fetch>", DateTime.Now.ToString("yyyy-MM-dd"));

            return _service.RetrieveMultiple(new FetchExpression(fetchXml));
        }

        public EntityCollection RetrieveEocsThatHaveReachedEndDate()
        {
            string fetchXml = String.Format(
                @"<fetch version='1.0' output-format='xml-platform' mapping='logical' distinct='false'>
                    <entity name='cncy_endofcertification'>
                    <attribute name='cncy_endofcertificationid' />
                    <attribute name='cncy_businessadministrator' />
                    <attribute name='cncy_contact' />
                    <attribute name='cncy_enddate' />
                    <attribute name='cncy_ended' />
                    <attribute name='cncy_name' />
                    <attribute name='cncy_processed' />
                    <attribute name='cncy_startdate' />
                    <attribute name='statecode' />
                    <attribute name='cncy_stoppedtrainingdetail' />
                    <attribute name='cncy_stoppedtrainingreason' />
                    <order attribute='cncy_enddate' descending='true' />
                    <filter type='and'>
                        <condition attribute='cncy_enddate' operator='on-or-before' value='{0}' />
                        <condition attribute='cncy_processed' operator='eq' value='1' />
                        <condition attribute='cncy_ended' operator='eq' value='0' />
                    </filter>
                    </entity>
                </fetch>", DateTime.Now.ToString("yyyy-MM-dd"));

            return _service.RetrieveMultiple(new FetchExpression(fetchXml));
        }

        public EntityCollection GetCertYearSummariesToInactivateForContact(Guid contactID)
        {
            string fetchXml = String.Format(
                @"<fetch version='1.0' output-format='xml-platform' mapping='logical' distinct='false'>
                    <entity name='cncy_certyearsummary'>
                    <attribute name='cncy_status' />
                    <attribute name='cncy_mfeertn' />
                    <attribute name='cncy_mfeertnyear2' />
                    <attribute name='cncy_invoicesent_y1' />
                    <attribute name='cncy_invoicesent_y2' />
                    <attribute name='cncy_invoicepaid_y1' />
                    <attribute name='cncy_invoicepaid_y2' />
                    <attribute name='cncy_certyearsummaryid' />
                    <filter type='and'>
                        <condition attribute='cncy_status' operator='not-in'>
                            <value>{1}</value>
                            <value>{2}</value>
                        </condition>
                        <condition attribute='cncy_contact' operator='eq' value='{0}' />
                    </filter>
                    </entity>
                </fetch>", contactID,
                Environment.GetEnvironmentVariable("CertYearInactiveStatus"),
                Environment.GetEnvironmentVariable("CertYearClosedStatus"));

            return _service.RetrieveMultiple(new FetchExpression(fetchXml));
        }

        public EntityCollection GetPendingYearsForContact(Guid contactID)
        {
            string fetchXml = String.Format(
                @"<fetch version='1.0' output-format='xml-platform' mapping='logical' distinct='false'>
                    <entity name='cncy_certyearsummary'>
                    <attribute name='cncy_status' />
                    <attribute name='cncy_mfeertn' />
                    <attribute name='cncy_mfeertnyear2' />
                    <attribute name='cncy_invoicesent_y1' />
                    <attribute name='cncy_invoicesent_y2' />
                    <attribute name='cncy_invoicepaid_y1' />
                    <attribute name='cncy_invoicepaid_y2' />
                    <attribute name='cncy_certyearsummaryid' />
                    <filter type='and'>
                        <condition attribute='cncy_status' operator='in'>
                            <value>{1}</value>
                        </condition>
                        <condition attribute='cncy_contact' operator='eq' value='{0}' />
                    </filter>
                    </entity>
                </fetch>", contactID,
                Environment.GetEnvironmentVariable("CertYearPendingStatus"));

            return _service.RetrieveMultiple(new FetchExpression(fetchXml));
        }


        public EntityCollection GetInactiveCertYearsWithFutureEndDateForContact(Guid contactID)
        {
            string fetchXml = String.Format(
                @"<fetch version='1.0' output-format='xml-platform' mapping='logical' distinct='false'>
            <entity name='cncy_certyearsummary'>
                <attribute name='cncy_certyearsummaryid' />
                <attribute name='cncy_status' />
                <attribute name='cncy_enddate' />
                <filter type='and'>
                    <condition attribute='cncy_contact' operator='eq' value='{0}' />
                    <condition attribute='cncy_status' operator='eq' value='{1}' />
                    <condition attribute='cncy_enddate' operator='on-or-after' value='{2}' />
                </filter>
            </entity>
        </fetch>",
                contactID,
                Environment.GetEnvironmentVariable("CertYearInactiveStatus"),
                DateTime.UtcNow.ToString("yyyy-MM-dd"));

            return _service.RetrieveMultiple(new FetchExpression(fetchXml));
        }

        public Entity RetrieveEmploymentHistory(Entity contact)
        {
            string fetchXml = String.Format(
                @"<fetch version='1.0' output-format='xml-platform' mapping='logical' distinct='false' top='1'>
                    <entity name='cncy_employment_history'>
                        <attribute name='cncy_employment_historyid' />
                        <attribute name='cncy_enddate' />
                        <attribute name='cncy_donotcall' />
                        <attribute name='cncy_donotmail' />
                        <attribute name='cncy_donotfax' />
                        <attribute name='cncy_donotemail' />
                        <order attribute='cncy_enddate' descending='true' />
                        <filter type='and'>
                            <condition attribute='cncy_contact' operator='eq' value='{0}' />
                        </filter>
                      </entity>
                </fetch>", contact.Id);

            EntityCollection employmentHistories = _service.RetrieveMultiple(new FetchExpression(fetchXml));
            return employmentHistories.Entities.Count == 1 ? employmentHistories.Entities[0] : null;
        }

        public void CreateEmploymentHistoryRecord(Entity contact)
        {
            Entity employmentHistory = new("cncy_employment_history");
            employmentHistory.Attributes.Add("cncy_enddate", DateTime.Today);
            employmentHistory.Attributes.Add("cncy_contact", new EntityReference("contact", contact.Id));
            employmentHistory = PopulateAttributeWithNullCheck(employmentHistory, "cncy_account", contact, "parentcustomerid");
            employmentHistory = PopulateAttributeWithNullCheck(employmentHistory, "cncy_workphone", contact, "telephone1");
            employmentHistory = PopulateAttributeWithNullCheck(employmentHistory, "cncy_addresstype", contact, "cpi_address1type");
            employmentHistory = PopulateAttributeWithNullCheck(employmentHistory, "cncy_street1", contact, "address1_line1");
            employmentHistory = PopulateAttributeWithNullCheck(employmentHistory, "cncy_city", contact, "address1_city");
            employmentHistory = PopulateAttributeWithNullCheck(employmentHistory, "cncy_state", contact, "cpi_address1state");
            employmentHistory = PopulateAttributeWithNullCheck(employmentHistory, "cncy_country", contact, "cpi_address1country");
            employmentHistory = PopulateAttributeWithNullCheck(employmentHistory, "cncy_postalcode", contact, "address1_postalcode");
            employmentHistory = PopulateAttributeWithNullCheck(employmentHistory, "cncy_email", contact, "emailaddress1");
            employmentHistory = PopulateAttributeWithNullCheck(employmentHistory, "cncy_donotemail", contact, "donotemail");
            employmentHistory = PopulateAttributeWithNullCheck(employmentHistory, "cncy_donotfax", contact, "donotfax");
            employmentHistory = PopulateAttributeWithNullCheck(employmentHistory, "cncy_donotcall", contact, "donotphone");
            employmentHistory = PopulateAttributeWithNullCheck(employmentHistory, "cncy_donotmail", contact, "donotpostalmail");

            _service.Create(employmentHistory);
        }

        public Entity PopulateAttributeWithNullCheck(Entity entityToPopulate, string attributeToPopulate, Entity entityToPopulateFrom, string attributeToPopulateFrom)
        {
            if (entityToPopulateFrom.Attributes.Contains(attributeToPopulateFrom) && entityToPopulateFrom.Attributes[attributeToPopulateFrom] != null)
            {
                entityToPopulate.Attributes.Add(attributeToPopulate, entityToPopulateFrom.Attributes[attributeToPopulateFrom]);
            }

            return entityToPopulate;
        }

        public Entity UpdateCommunicationDetails(Entity contact, bool doNotAllow)
        {
            contact.Attributes["donotemail"] = doNotAllow;
            contact.Attributes["donotfax"] = doNotAllow;
            contact.Attributes["donotphone"] = doNotAllow;
            contact.Attributes["donotpostalmail"] = doNotAllow;

            return contact;
        }

        public Entity RevertCommunicationUpdates(Entity contact, Entity employmentHistory)
        {
            if (employmentHistory.Contains("cncy_donotemail") && employmentHistory.Attributes["cncy_donotemail"] != null)
            {
                contact.Attributes["donotemail"] = employmentHistory.Attributes["cncy_donotemail"];
            }

            if (employmentHistory.Contains("cncy_donotfax") && employmentHistory.Attributes["cncy_donotfax"] != null)
            {
                contact.Attributes["donotfax"] = employmentHistory.Attributes["cncy_donotfax"];
            }

            if (employmentHistory.Contains("cncy_donotcall") && employmentHistory.Attributes["cncy_donotcall"] != null)
            {
                contact.Attributes["donotphone"] = employmentHistory.Attributes["cncy_donotcall"];
            }

            if (employmentHistory.Contains("cncy_donotmail") && employmentHistory.Attributes["cncy_donotmail"] != null)
            {
                contact.Attributes["donotpostalmail"] = employmentHistory.Attributes["cncy_donotmail"];
            }

            return contact;
        }

        public Entity UpdateEocStatus(Entity eoc, bool processed, bool ended)
        {
            eoc.Attributes["cncy_processed"] = processed;
            eoc.Attributes["cncy_ended"] = ended;

            return eoc;
        }

        public Entity UpdateExcludeFromDocumentationDetails(Entity contact, bool exclude)
        {
            contact.Attributes["cncy_excludefromdocumentation"] = exclude;
            contact.Attributes["cncy_excludefromdocumentationdate"] = exclude ? DateTime.Now : null;

            return contact;
        }
    }
}