using System.Text.Json;
using AutoMapper;
using Erao.Core.DTOs.Connector;
using Erao.Core.Entities;
using Erao.Core.Enums;
using Erao.Core.Interfaces;

namespace Erao.Application.Services;

public interface IConnectorService
{
    Task<IEnumerable<AppConnectorDto>> GetConnectorsAsync(Guid userId);
    Task<AppConnectorDto?> GetByIdAsync(Guid id, Guid userId);
    Task<AppConnectorDto> CreateConnectorAsync(Guid userId, CreateConnectorRequest request);
    Task<AppConnectorDto> UpdateConnectorAsync(Guid userId, Guid connectorId, UpdateConnectorRequest request);
    Task DeleteConnectorAsync(Guid userId, Guid connectorId);
    List<ConnectorMetadataDto> GetAllMetadata();
}

public class ConnectorService : IConnectorService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly IEncryptionService _encryptionService;
    private readonly IMapper _mapper;

    public ConnectorService(IUnitOfWork unitOfWork, IEncryptionService encryptionService, IMapper mapper)
    {
        _unitOfWork = unitOfWork;
        _encryptionService = encryptionService;
        _mapper = mapper;
    }

    public async Task<IEnumerable<AppConnectorDto>> GetConnectorsAsync(Guid userId)
    {
        var connectors = await _unitOfWork.AppConnectors.GetByUserIdAsync(userId);
        return _mapper.Map<IEnumerable<AppConnectorDto>>(connectors);
    }

    public async Task<AppConnectorDto?> GetByIdAsync(Guid id, Guid userId)
    {
        var connector = await _unitOfWork.AppConnectors.GetByIdAsync(id);
        if (connector == null || connector.UserId != userId)
        {
            return null;
        }
        return _mapper.Map<AppConnectorDto>(connector);
    }

    public async Task<AppConnectorDto> CreateConnectorAsync(Guid userId, CreateConnectorRequest request)
    {
        if (!Enum.IsDefined(typeof(ConnectorType), request.ConnectorType))
        {
            throw new InvalidOperationException("Invalid connector type");
        }

        var connectorType = (ConnectorType)request.ConnectorType;

        // Encrypt credentials as JSON
        var credentialsJson = JsonSerializer.Serialize(request.Credentials);
        var encryptedCredentials = _encryptionService.Encrypt(credentialsJson);

        var connector = new AppConnector
        {
            UserId = userId,
            Name = request.Name,
            ConnectorType = connectorType,
            EncryptedCredentials = encryptedCredentials,
            IsActive = true,
            SchemaContext = GetSchemaTemplate(connectorType)
        };

        await _unitOfWork.AppConnectors.AddAsync(connector);
        await _unitOfWork.SaveChangesAsync();

        return _mapper.Map<AppConnectorDto>(connector);
    }

    public async Task<AppConnectorDto> UpdateConnectorAsync(Guid userId, Guid connectorId, UpdateConnectorRequest request)
    {
        var connector = await _unitOfWork.AppConnectors.GetByIdAsync(connectorId);
        if (connector == null || connector.UserId != userId)
        {
            throw new InvalidOperationException("Connector not found");
        }

        if (!string.IsNullOrEmpty(request.Name))
        {
            connector.Name = request.Name;
        }

        if (request.Credentials != null)
        {
            var credentialsJson = JsonSerializer.Serialize(request.Credentials);
            connector.EncryptedCredentials = _encryptionService.Encrypt(credentialsJson);
            // Invalidate schema cache on credential change
            connector.SchemaContext = GetSchemaTemplate(connector.ConnectorType);
        }

        await _unitOfWork.AppConnectors.UpdateAsync(connector);
        await _unitOfWork.SaveChangesAsync();

        return _mapper.Map<AppConnectorDto>(connector);
    }

    public async Task DeleteConnectorAsync(Guid userId, Guid connectorId)
    {
        var connector = await _unitOfWork.AppConnectors.GetByIdAsync(connectorId);
        if (connector == null || connector.UserId != userId)
        {
            throw new InvalidOperationException("Connector not found");
        }

        await _unitOfWork.AppConnectors.DeleteAsync(connector);
        await _unitOfWork.SaveChangesAsync();
    }

    public List<ConnectorMetadataDto> GetAllMetadata()
    {
        return ConnectorMetadataRegistry.GetAll();
    }

    private static string GetSchemaTemplate(ConnectorType type) => type switch
    {
        ConnectorType.Shopify => ShopifySchema,
        ConnectorType.Stripe => StripeSchema,
        ConnectorType.WooCommerce => WooCommerceSchema,
        ConnectorType.QuickBooks => QuickBooksSchema,
        ConnectorType.HubSpot => HubSpotSchema,
        ConnectorType.Salesforce => SalesforceSchema,
        ConnectorType.GoogleAnalytics => GoogleAnalyticsSchema,
        ConnectorType.Notion => NotionSchema,
        ConnectorType.Airtable => AirtableSchema,
        ConnectorType.GoogleSheets => GoogleSheetsSchema,
        _ => ""
    };

    private const string ShopifySchema = @"-- Shopify Store Data
-- orders (Sales orders placed in your store)
CREATE TABLE ""orders"" (
    ""id"" BIGINT NOT NULL,              -- Unique order ID
    ""order_number"" INTEGER,            -- Customer-facing #1001, #1002...
    ""email"" TEXT,                      -- Customer email
    ""total_price"" DECIMAL NOT NULL,    -- Total including tax & shipping
    ""subtotal_price"" DECIMAL,          -- Subtotal before tax & shipping
    ""total_tax"" DECIMAL,              -- Total tax amount
    ""total_discounts"" DECIMAL,        -- Total discounts applied
    ""financial_status"" TEXT,           -- pending | paid | refunded | voided
    ""fulfillment_status"" TEXT,         -- unfulfilled | partial | fulfilled
    ""currency"" TEXT,                   -- Currency code (USD, EUR, etc.)
    ""created_at"" TIMESTAMP NOT NULL,   -- When order was placed
    ""updated_at"" TIMESTAMP,           -- Last update time
    ""cancelled_at"" TIMESTAMP,         -- When cancelled (NULL if not)
    ""customer_id"" BIGINT              -- FK to customers table
);

-- products (Items available in your store)
CREATE TABLE ""products"" (
    ""id"" BIGINT NOT NULL,
    ""title"" TEXT NOT NULL,             -- Product name
    ""vendor"" TEXT,                     -- Brand / manufacturer
    ""product_type"" TEXT,              -- Category (e.g., 'Shirts', 'Electronics')
    ""status"" TEXT,                     -- active | draft | archived
    ""created_at"" TIMESTAMP NOT NULL,
    ""updated_at"" TIMESTAMP,
    ""published_at"" TIMESTAMP          -- When made visible to customers
);

-- customers (People who have accounts or placed orders)
CREATE TABLE ""customers"" (
    ""id"" BIGINT NOT NULL,
    ""email"" TEXT,
    ""first_name"" TEXT,
    ""last_name"" TEXT,
    ""orders_count"" INTEGER,           -- Total orders placed
    ""total_spent"" DECIMAL,            -- Lifetime spend
    ""created_at"" TIMESTAMP NOT NULL,
    ""updated_at"" TIMESTAMP
);

-- inventory (Stock levels per variant per location)
CREATE TABLE ""inventory"" (
    ""inventory_item_id"" BIGINT NOT NULL,
    ""variant_id"" BIGINT,
    ""product_id"" BIGINT,
    ""sku"" TEXT,
    ""available"" INTEGER,              -- Units available to sell
    ""location_id"" BIGINT
);";

    private const string StripeSchema = @"-- Stripe Payments Data
-- payments (Successful and attempted charges)
CREATE TABLE ""payments"" (
    ""id"" TEXT NOT NULL,                -- Charge ID (ch_xxx)
    ""amount"" INTEGER NOT NULL,         -- Amount in cents
    ""currency"" TEXT NOT NULL,          -- 3-letter currency code
    ""status"" TEXT NOT NULL,            -- succeeded | pending | failed
    ""description"" TEXT,
    ""customer_id"" TEXT,               -- FK to customers (cus_xxx)
    ""payment_method"" TEXT,            -- card | bank_transfer | etc.
    ""created_at"" TIMESTAMP NOT NULL,
    ""receipt_email"" TEXT
);

-- subscriptions (Recurring billing)
CREATE TABLE ""subscriptions"" (
    ""id"" TEXT NOT NULL,                -- Subscription ID (sub_xxx)
    ""customer_id"" TEXT NOT NULL,
    ""status"" TEXT NOT NULL,            -- active | past_due | canceled | trialing
    ""plan_id"" TEXT,                   -- Price/plan identifier
    ""plan_amount"" INTEGER,            -- Amount in cents per interval
    ""plan_interval"" TEXT,             -- month | year | week | day
    ""current_period_start"" TIMESTAMP,
    ""current_period_end"" TIMESTAMP,
    ""cancel_at_period_end"" BOOLEAN,
    ""created_at"" TIMESTAMP NOT NULL
);

-- customers (Stripe customer records)
CREATE TABLE ""customers"" (
    ""id"" TEXT NOT NULL,                -- Customer ID (cus_xxx)
    ""email"" TEXT,
    ""name"" TEXT,
    ""created_at"" TIMESTAMP NOT NULL,
    ""balance"" INTEGER,                -- Account balance in cents
    ""currency"" TEXT,
    ""delinquent"" BOOLEAN             -- Has unpaid invoices
);

-- invoices (Billing documents)
CREATE TABLE ""invoices"" (
    ""id"" TEXT NOT NULL,                -- Invoice ID (in_xxx)
    ""customer_id"" TEXT NOT NULL,
    ""subscription_id"" TEXT,
    ""amount_due"" INTEGER NOT NULL,    -- Amount due in cents
    ""amount_paid"" INTEGER,
    ""status"" TEXT,                     -- draft | open | paid | void | uncollectible
    ""currency"" TEXT,
    ""created_at"" TIMESTAMP NOT NULL,
    ""due_date"" TIMESTAMP,
    ""paid_at"" TIMESTAMP
);";

    private const string WooCommerceSchema = @"-- WooCommerce Store Data
-- orders (Customer orders)
CREATE TABLE ""orders"" (
    ""id"" BIGINT NOT NULL,
    ""order_number"" TEXT,
    ""status"" TEXT,                     -- pending | processing | on-hold | completed | cancelled | refunded | failed
    ""total"" DECIMAL NOT NULL,
    ""subtotal"" DECIMAL,
    ""total_tax"" DECIMAL,
    ""discount_total"" DECIMAL,
    ""shipping_total"" DECIMAL,
    ""payment_method"" TEXT,
    ""customer_id"" BIGINT,
    ""billing_email"" TEXT,
    ""currency"" TEXT,
    ""date_created"" TIMESTAMP NOT NULL,
    ""date_modified"" TIMESTAMP
);

-- products (Store products)
CREATE TABLE ""products"" (
    ""id"" BIGINT NOT NULL,
    ""name"" TEXT NOT NULL,
    ""type"" TEXT,                       -- simple | variable | grouped | external
    ""status"" TEXT,                     -- publish | draft | pending | private
    ""sku"" TEXT,
    ""price"" DECIMAL,
    ""regular_price"" DECIMAL,
    ""sale_price"" DECIMAL,
    ""stock_quantity"" INTEGER,
    ""stock_status"" TEXT,              -- instock | outofstock | onbackorder
    ""categories"" TEXT,
    ""date_created"" TIMESTAMP NOT NULL
);

-- customers (Registered customers)
CREATE TABLE ""customers"" (
    ""id"" BIGINT NOT NULL,
    ""email"" TEXT,
    ""first_name"" TEXT,
    ""last_name"" TEXT,
    ""orders_count"" INTEGER,
    ""total_spent"" DECIMAL,
    ""date_created"" TIMESTAMP NOT NULL
);

-- coupons (Discount coupons)
CREATE TABLE ""coupons"" (
    ""id"" BIGINT NOT NULL,
    ""code"" TEXT NOT NULL,
    ""discount_type"" TEXT,             -- percent | fixed_cart | fixed_product
    ""amount"" DECIMAL,
    ""usage_count"" INTEGER,
    ""usage_limit"" INTEGER,
    ""date_created"" TIMESTAMP NOT NULL,
    ""date_expires"" TIMESTAMP
);";

    private const string QuickBooksSchema = @"-- QuickBooks Accounting Data
-- invoices (Sales invoices sent to customers)
CREATE TABLE ""invoices"" (
    ""id"" TEXT NOT NULL,
    ""doc_number"" TEXT,                -- Invoice number
    ""customer_id"" TEXT,
    ""customer_name"" TEXT,
    ""total_amount"" DECIMAL NOT NULL,
    ""balance"" DECIMAL,               -- Amount still owed
    ""due_date"" DATE,
    ""status"" TEXT,                    -- Paid | Open | Overdue | Voided
    ""created_at"" TIMESTAMP NOT NULL
);

-- expenses (Bills and expenses)
CREATE TABLE ""expenses"" (
    ""id"" TEXT NOT NULL,
    ""payment_type"" TEXT,             -- Cash | Check | CreditCard
    ""account_name"" TEXT,
    ""vendor_name"" TEXT,
    ""total_amount"" DECIMAL NOT NULL,
    ""category"" TEXT,
    ""created_at"" TIMESTAMP NOT NULL
);

-- accounts (Chart of accounts)
CREATE TABLE ""accounts"" (
    ""id"" TEXT NOT NULL,
    ""name"" TEXT NOT NULL,
    ""account_type"" TEXT,             -- Bank | CreditCard | Income | Expense | etc.
    ""current_balance"" DECIMAL,
    ""currency"" TEXT
);

-- profit_and_loss (P&L summary)
CREATE TABLE ""profit_and_loss"" (
    ""period"" TEXT NOT NULL,           -- e.g., '2025-01'
    ""total_income"" DECIMAL,
    ""total_cogs"" DECIMAL,            -- Cost of goods sold
    ""gross_profit"" DECIMAL,
    ""total_expenses"" DECIMAL,
    ""net_income"" DECIMAL
);";

    private const string HubSpotSchema = @"-- HubSpot CRM Data
-- contacts (People in your CRM)
CREATE TABLE ""contacts"" (
    ""id"" BIGINT NOT NULL,
    ""email"" TEXT,
    ""first_name"" TEXT,
    ""last_name"" TEXT,
    ""phone"" TEXT,
    ""company"" TEXT,
    ""lifecycle_stage"" TEXT,           -- subscriber | lead | mql | sql | opportunity | customer
    ""lead_status"" TEXT,              -- New | Open | In Progress | Qualified | Unqualified
    ""source"" TEXT,                    -- Organic Search | Paid | Referral | Direct | etc.
    ""created_at"" TIMESTAMP NOT NULL
);

-- deals (Sales opportunities)
CREATE TABLE ""deals"" (
    ""id"" BIGINT NOT NULL,
    ""deal_name"" TEXT NOT NULL,
    ""amount"" DECIMAL,
    ""stage"" TEXT,                     -- Pipeline stage name
    ""pipeline"" TEXT,                 -- Pipeline name
    ""close_date"" DATE,
    ""owner_id"" BIGINT,
    ""contact_id"" BIGINT,
    ""company_id"" BIGINT,
    ""created_at"" TIMESTAMP NOT NULL
);

-- companies (Organizations)
CREATE TABLE ""companies"" (
    ""id"" BIGINT NOT NULL,
    ""name"" TEXT NOT NULL,
    ""domain"" TEXT,
    ""industry"" TEXT,
    ""annual_revenue"" DECIMAL,
    ""number_of_employees"" INTEGER,
    ""city"" TEXT,
    ""country"" TEXT,
    ""created_at"" TIMESTAMP NOT NULL
);

-- tickets (Support tickets)
CREATE TABLE ""tickets"" (
    ""id"" BIGINT NOT NULL,
    ""subject"" TEXT NOT NULL,
    ""status"" TEXT,                    -- New | Waiting | In Progress | Closed
    ""priority"" TEXT,                 -- Low | Medium | High | Urgent
    ""category"" TEXT,
    ""contact_id"" BIGINT,
    ""created_at"" TIMESTAMP NOT NULL,
    ""closed_at"" TIMESTAMP
);";

    private const string SalesforceSchema = @"-- Salesforce CRM Data
-- leads (Potential customers)
CREATE TABLE ""leads"" (
    ""id"" TEXT NOT NULL,
    ""name"" TEXT,
    ""email"" TEXT,
    ""company"" TEXT,
    ""title"" TEXT,
    ""status"" TEXT,                    -- Open | Working | Closed-Converted | Closed-Not Converted
    ""source"" TEXT,                    -- Web | Phone | Partner | etc.
    ""industry"" TEXT,
    ""annual_revenue"" DECIMAL,
    ""created_at"" TIMESTAMP NOT NULL,
    ""converted_at"" TIMESTAMP
);

-- opportunities (Sales deals)
CREATE TABLE ""opportunities"" (
    ""id"" TEXT NOT NULL,
    ""name"" TEXT NOT NULL,
    ""amount"" DECIMAL,
    ""stage"" TEXT,                     -- Prospecting | Qualification | Proposal | Negotiation | Closed Won | Closed Lost
    ""probability"" INTEGER,           -- Win probability percentage
    ""close_date"" DATE,
    ""account_id"" TEXT,
    ""owner_id"" TEXT,
    ""type"" TEXT,                      -- New Business | Existing Business
    ""created_at"" TIMESTAMP NOT NULL
);

-- accounts (Companies / organizations)
CREATE TABLE ""accounts"" (
    ""id"" TEXT NOT NULL,
    ""name"" TEXT NOT NULL,
    ""industry"" TEXT,
    ""type"" TEXT,                      -- Prospect | Customer | Partner | etc.
    ""annual_revenue"" DECIMAL,
    ""number_of_employees"" INTEGER,
    ""billing_city"" TEXT,
    ""billing_country"" TEXT,
    ""created_at"" TIMESTAMP NOT NULL
);

-- cases (Customer support cases)
CREATE TABLE ""cases"" (
    ""id"" TEXT NOT NULL,
    ""subject"" TEXT,
    ""status"" TEXT,                    -- New | Working | Escalated | Closed
    ""priority"" TEXT,                 -- Low | Medium | High | Critical
    ""type"" TEXT,                      -- Question | Problem | Feature Request
    ""account_id"" TEXT,
    ""contact_id"" TEXT,
    ""created_at"" TIMESTAMP NOT NULL,
    ""closed_at"" TIMESTAMP
);";

    private const string GoogleAnalyticsSchema = @"-- Google Analytics Data
-- sessions (Website traffic sessions)
CREATE TABLE ""sessions"" (
    ""date"" DATE NOT NULL,
    ""source"" TEXT,                    -- google | facebook | direct | etc.
    ""medium"" TEXT,                    -- organic | cpc | referral | none | email
    ""campaign"" TEXT,                 -- Campaign name
    ""sessions"" INTEGER NOT NULL,
    ""users"" INTEGER,
    ""new_users"" INTEGER,
    ""bounce_rate"" DECIMAL,           -- Percentage (0-100)
    ""avg_session_duration"" DECIMAL,  -- In seconds
    ""pages_per_session"" DECIMAL
);

-- pageviews (Page-level metrics)
CREATE TABLE ""pageviews"" (
    ""date"" DATE NOT NULL,
    ""page_path"" TEXT NOT NULL,
    ""page_title"" TEXT,
    ""pageviews"" INTEGER NOT NULL,
    ""unique_pageviews"" INTEGER,
    ""avg_time_on_page"" DECIMAL,     -- In seconds
    ""entrances"" INTEGER,
    ""exits"" INTEGER,
    ""exit_rate"" DECIMAL
);

-- conversions (Goal completions and events)
CREATE TABLE ""conversions"" (
    ""date"" DATE NOT NULL,
    ""goal_name"" TEXT,
    ""completions"" INTEGER NOT NULL,
    ""value"" DECIMAL,                 -- Goal value
    ""conversion_rate"" DECIMAL,       -- Percentage
    ""source"" TEXT,
    ""medium"" TEXT
);";

    private const string NotionSchema = @"-- Notion Workspace Data
-- databases (Notion databases / collections)
CREATE TABLE ""databases"" (
    ""id"" TEXT NOT NULL,
    ""title"" TEXT NOT NULL,
    ""created_at"" TIMESTAMP NOT NULL,
    ""last_edited_at"" TIMESTAMP,
    ""created_by"" TEXT,
    ""property_count"" INTEGER         -- Number of properties/columns
);

-- pages (Pages within databases or standalone)
CREATE TABLE ""pages"" (
    ""id"" TEXT NOT NULL,
    ""title"" TEXT,
    ""database_id"" TEXT,              -- Parent database (NULL for standalone pages)
    ""parent_page_id"" TEXT,           -- Parent page for nested pages
    ""created_at"" TIMESTAMP NOT NULL,
    ""last_edited_at"" TIMESTAMP,
    ""created_by"" TEXT,
    ""archived"" BOOLEAN
);";

    private const string AirtableSchema = @"-- Airtable Data
-- bases (Airtable bases / workspaces)
CREATE TABLE ""bases"" (
    ""id"" TEXT NOT NULL,
    ""name"" TEXT NOT NULL,
    ""table_count"" INTEGER
);

-- tables (Tables within bases)
CREATE TABLE ""tables"" (
    ""id"" TEXT NOT NULL,
    ""base_id"" TEXT NOT NULL,
    ""name"" TEXT NOT NULL,
    ""field_count"" INTEGER,
    ""record_count"" INTEGER
);

-- records (Rows in tables — schema varies per table)
CREATE TABLE ""records"" (
    ""id"" TEXT NOT NULL,
    ""table_id"" TEXT NOT NULL,
    ""created_at"" TIMESTAMP,
    ""fields"" JSONB                   -- Dynamic fields as key-value pairs
);";

    private const string GoogleSheetsSchema = @"-- Google Sheets Data
-- sheets (Individual sheets/tabs)
CREATE TABLE ""sheets"" (
    ""spreadsheet_id"" TEXT NOT NULL,
    ""sheet_id"" INTEGER NOT NULL,
    ""title"" TEXT NOT NULL,
    ""row_count"" INTEGER,
    ""column_count"" INTEGER
);

-- rows (Data rows — columns are dynamic based on headers)
CREATE TABLE ""rows"" (
    ""spreadsheet_id"" TEXT NOT NULL,
    ""sheet_title"" TEXT NOT NULL,
    ""row_number"" INTEGER NOT NULL,
    ""data"" JSONB                     -- Row data as key-value pairs from column headers
);";
}
