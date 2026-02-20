using Erao.Core.DTOs.Connector;
using Erao.Core.Enums;

namespace Erao.Application.Services;

public static class ConnectorMetadataRegistry
{
    public static List<ConnectorMetadataDto> GetAll() => new()
    {
        new ConnectorMetadataDto
        {
            ConnectorType = (int)ConnectorType.Shopify,
            Name = "Shopify",
            Category = "E-Commerce",
            Description = "Connect your Shopify store to analyze orders, products, customers, and inventory data.",
            IconSlug = "shopify",
            IsAvailable = true,
            CredentialFields = new()
            {
                new() { Key = "storeUrl", Label = "Store URL", Type = "url", Placeholder = "mystore.myshopify.com", Required = true, HelpText = "Your Shopify store domain" },
                new() { Key = "apiKey", Label = "Admin API Token", Type = "password", Placeholder = "shpat_...", Required = true, HelpText = "Found in Settings > Apps > Develop apps" }
            },
            DataTables = new() { "Orders", "Products", "Customers", "Inventory" }
        },
        new ConnectorMetadataDto
        {
            ConnectorType = (int)ConnectorType.Stripe,
            Name = "Stripe",
            Category = "Payments",
            Description = "Connect Stripe to analyze payments, subscriptions, customers, and invoices.",
            IconSlug = "stripe",
            IsAvailable = true,
            CredentialFields = new()
            {
                new() { Key = "secretKey", Label = "Secret Key", Type = "password", Placeholder = "sk_live_...", Required = true, HelpText = "Found in Developers > API keys" }
            },
            DataTables = new() { "Payments", "Subscriptions", "Customers", "Invoices" }
        },
        new ConnectorMetadataDto
        {
            ConnectorType = (int)ConnectorType.WooCommerce,
            Name = "WooCommerce",
            Category = "E-Commerce",
            Description = "Connect your WooCommerce store to analyze orders, products, customers, and coupons.",
            IconSlug = "woocommerce",
            IsAvailable = true,
            CredentialFields = new()
            {
                new() { Key = "storeUrl", Label = "Store URL", Type = "url", Placeholder = "https://mystore.com", Required = true, HelpText = "Your WordPress/WooCommerce site URL" },
                new() { Key = "consumerKey", Label = "Consumer Key", Type = "password", Placeholder = "ck_...", Required = true, HelpText = "Found in WooCommerce > Settings > Advanced > REST API" },
                new() { Key = "consumerSecret", Label = "Consumer Secret", Type = "password", Placeholder = "cs_...", Required = true }
            },
            DataTables = new() { "Orders", "Products", "Customers", "Coupons" }
        },
        new ConnectorMetadataDto
        {
            ConnectorType = (int)ConnectorType.QuickBooks,
            Name = "QuickBooks Online",
            Category = "Accounting",
            Description = "Connect QuickBooks to analyze invoices, expenses, accounts, and profit & loss data.",
            IconSlug = "quickbooks",
            IsAvailable = true,
            CredentialFields = new()
            {
                new() { Key = "accessToken", Label = "Access Token", Type = "password", Placeholder = "eyJ0eXAi...", Required = true, HelpText = "OAuth access token from QuickBooks Developer playground" },
                new() { Key = "realmId", Label = "Realm ID (Company ID)", Type = "text", Placeholder = "123456789", Required = true, HelpText = "Found in your QuickBooks company URL" }
            },
            DataTables = new() { "Invoices", "Expenses", "Accounts", "Profit & Loss" }
        },
        new ConnectorMetadataDto
        {
            ConnectorType = (int)ConnectorType.HubSpot,
            Name = "HubSpot",
            Category = "CRM",
            Description = "Connect HubSpot to analyze contacts, deals, companies, and support tickets.",
            IconSlug = "hubspot",
            IsAvailable = true,
            CredentialFields = new()
            {
                new() { Key = "privateAppToken", Label = "Private App Token", Type = "password", Placeholder = "pat-na1-...", Required = true, HelpText = "Found in Settings > Integrations > Private Apps" }
            },
            DataTables = new() { "Contacts", "Deals", "Companies", "Tickets" }
        },
        new ConnectorMetadataDto
        {
            ConnectorType = (int)ConnectorType.Salesforce,
            Name = "Salesforce",
            Category = "CRM",
            Description = "Connect Salesforce to analyze leads, opportunities, accounts, and cases.",
            IconSlug = "salesforce",
            IsAvailable = true,
            CredentialFields = new()
            {
                new() { Key = "instanceUrl", Label = "Instance URL", Type = "url", Placeholder = "https://mycompany.salesforce.com", Required = true, HelpText = "Your Salesforce org URL" },
                new() { Key = "accessToken", Label = "Access Token", Type = "password", Placeholder = "00D...", Required = true, HelpText = "OAuth access token or session ID" }
            },
            DataTables = new() { "Leads", "Opportunities", "Accounts", "Cases" }
        },
        new ConnectorMetadataDto
        {
            ConnectorType = (int)ConnectorType.GoogleAnalytics,
            Name = "Google Analytics",
            Category = "Analytics",
            Description = "Connect Google Analytics to analyze sessions, pageviews, and conversion data.",
            IconSlug = "google-analytics",
            IsAvailable = true,
            CredentialFields = new()
            {
                new() { Key = "propertyId", Label = "Property ID", Type = "text", Placeholder = "123456789", Required = true, HelpText = "GA4 property ID from Admin > Property Settings" },
                new() { Key = "serviceAccountJson", Label = "Service Account JSON", Type = "password", Placeholder = "Paste the full JSON key file content", Required = true, HelpText = "Download from Google Cloud Console > IAM > Service Accounts" }
            },
            DataTables = new() { "Sessions", "Pageviews", "Conversions" }
        },
        new ConnectorMetadataDto
        {
            ConnectorType = (int)ConnectorType.Notion,
            Name = "Notion",
            Category = "Productivity",
            Description = "Connect Notion to analyze databases and pages in your workspace.",
            IconSlug = "notion",
            IsAvailable = true,
            CredentialFields = new()
            {
                new() { Key = "integrationToken", Label = "Integration Token", Type = "password", Placeholder = "secret_...", Required = true, HelpText = "Found in Settings > Connections > Develop or manage integrations" }
            },
            DataTables = new() { "Databases", "Pages" }
        },
        new ConnectorMetadataDto
        {
            ConnectorType = (int)ConnectorType.Airtable,
            Name = "Airtable",
            Category = "Databases",
            Description = "Connect Airtable to analyze bases, tables, and records.",
            IconSlug = "airtable",
            IsAvailable = true,
            CredentialFields = new()
            {
                new() { Key = "personalAccessToken", Label = "Personal Access Token", Type = "password", Placeholder = "pat...", Required = true, HelpText = "Found in Account > Developer hub" },
                new() { Key = "baseId", Label = "Base ID", Type = "text", Placeholder = "appXXXXXXXXXXXXXX", Required = true, HelpText = "Found in the base URL: airtable.com/appXXX" }
            },
            DataTables = new() { "Bases", "Tables", "Records" }
        },
        new ConnectorMetadataDto
        {
            ConnectorType = (int)ConnectorType.GoogleSheets,
            Name = "Google Sheets",
            Category = "Spreadsheets",
            Description = "Connect Google Sheets to analyze spreadsheet data.",
            IconSlug = "google-sheets",
            IsAvailable = true,
            CredentialFields = new()
            {
                new() { Key = "spreadsheetId", Label = "Spreadsheet ID", Type = "text", Placeholder = "1BxiMVs0XRA...", Required = true, HelpText = "Found in the spreadsheet URL between /d/ and /edit" },
                new() { Key = "serviceAccountJson", Label = "Service Account JSON", Type = "password", Placeholder = "Paste the full JSON key file content", Required = true, HelpText = "Download from Google Cloud Console > IAM > Service Accounts" }
            },
            DataTables = new() { "Sheets", "Rows" }
        }
    };
}
