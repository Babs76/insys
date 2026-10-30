/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Testing
 * CONFIDENTIALITY AND INTELLECTUAL PROPERTY NOTICE
 * This source code and its associated documentation contain confidential and
 * proprietary information belonging to OpenFactor Technology Group or its
 * applicable intellectual property rights holder.
 * This material is provided solely for authorised development, maintenance,
 * operation or evaluation purposes, as applicable. No person may reproduce,
 * modify, distribute, disclose, publish or otherwise use this material except
 * as expressly permitted by an applicable written agreement or licence.
 * No ownership rights or licence to any intellectual property are granted
 * by the inclusion of this notice, except as expressly provided in an
 * applicable written agreement or licence.
 * Third-party software, open-source components and customer-owned materials
 * remain subject to their respective ownership rights and applicable licences
 * or agreements.
 * UNAUTHORISED ACCESS, USE, COPYING OR DISCLOSURE IS PROHIBITED.
 * ============================================================================
 */
using Npgsql;

namespace Insys.Testing;

/// <summary>
/// The configuration that makes a tenant ready for every onboarding step (docs/specs/addenda/onboarding-readiness.md),
/// so tests can onboard and activate one. Each row fills its required columns from the schema, with the values a check
/// or a CHECK constraint needs given explicitly.
/// </summary>
public static class OnboardingSeed
{
    /// <summary>
    /// A static data row in force from <paramref name="from"/>, its NOT NULL columns without a default filled by type
    /// (text: the code; numbers: 1; booleans: true; dates: the day; arrays and JSON: empty), then <paramref name="fields"/>.
    /// </summary>
    public static async Task SeedStaticFilled(this TestDatabase database, Guid? tenantId, string table, string recordStatus, string code, DateOnly from,
        params (string Column, object Value)[] fields)
    {
        var (schema, name) = (table.Split('.')[0], table.Split('.')[1]);
        var given = fields.Select(f => f.Column).ToHashSet(StringComparer.Ordinal);
        string[] standard = ["id", "tenant_id", "code", "version", "effective_from", "effective_to", "record_status", "created_by", "change_reason", "created_at", "row_version",
            "record_class"];
        var filled = new List<(string Column, object Value)>(fields);
        await using (var connection = new NpgsqlConnection(database.OwnerConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand("""
                SELECT column_name, data_type, udt_name, character_maximum_length FROM information_schema.columns
                WHERE table_schema = @s AND table_name = @n AND is_nullable = 'NO' AND column_default IS NULL ORDER BY ordinal_position
                """, connection);
            command.Parameters.AddWithValue("s", schema);
            command.Parameters.AddWithValue("n", name);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var column = reader.GetString(0);
                if (standard.Contains(column) || given.Contains(column))
                {
                    continue;
                }

                var length = reader.IsDBNull(3) ? int.MaxValue : reader.GetInt32(3);
                filled.Add((column, (reader.GetString(1), reader.GetString(2)) switch
                {
                    ("ARRAY", _) => Array.Empty<string>(),
                    ("jsonb" or "json", _) => System.Text.Json.JsonDocument.Parse("{}").RootElement,
                    ("boolean", _) => true,
                    ("date", _) => from,
                    ("integer" or "smallint", _) => 1,
                    ("bigint", _) => 1L,
                    ("numeric", _) => 1m,
                    ("uuid", _) => Guid.CreateVersion7(),
                    ("interval", _) => TimeSpan.FromDays(1),
                    ("bytea", _) => new byte[] { 0 },
                    ("time without time zone", _) => TimeOnly.MinValue,
                    ("timestamp with time zone", _) => DateTimeOffset.UtcNow,
                    _ => code.Length <= length ? code : code[..length],
                }));
            }
        }

        await database.SeedStatic(tenantId, table, recordStatus, code, 1, from, null, [.. filled]);
    }

    /// <summary>The body completing an onboarding step: the steps whose checks allow it record no reinsurance and no webhooks, with a reason.</summary>
    public static object StepBody(string step) => step switch
    {
        "REINSURANCE" => new { step, attestations = new[] { new { check = "TREATIES", note = "No reinsurance: test tenant" } } },
        "INTEGRATION_AND_REPORTING" => new { step, attestations = new[] { new { check = "WEBHOOKS_OR_NONE", note = "No partners: test tenant" } } },
        _ => new { step },
    };

    /// <summary>
    /// Completes the onboarding steps from the current one to the end, recording the end-to-end run with
    /// <paramref name="recorder"/> and approving it with <paramref name="approver"/> (a second platform operator); returns the ETag.
    /// </summary>
    public static async Task<string> CompleteOnboarding(this HttpClient recorder, HttpClient approver, string url, string etag)
    {
        var tenant = await (await recorder.GetAsync(url)).Json();
        var steps = tenant.GetProperty("onboardingSteps").EnumerateArray().Select(s => s.GetString()!).ToList();
        foreach (var step in steps.SkipWhile(s => s != tenant.GetProperty("onboardingStep").GetString()))
        {
            if (step == "END_TO_END_TESTS")
            {
                etag = await recorder.RecordSmokeRun(approver, url, etag);
            }

            var completed = await recorder.Send(HttpMethod.Post, $"{url}/complete-onboarding-step", StepBody(step), ifMatch: etag);
            await Expect(completed, System.Net.HttpStatusCode.NoContent, step);
            etag = completed.ETag();
        }

        return etag;
    }

    /// <summary>Records a passed end-to-end run and has a second platform operator approve it; returns the ETag.</summary>
    public static async Task<string> RecordSmokeRun(this HttpClient recorder, HttpClient approver, string url, string etag)
    {
        var recorded = await recorder.Send(HttpMethod.Post, $"{url}/smoke-run",
            new { runReference = "UAT-RUN-1", runDate = TestClock.Today, outcome = "PASSED", ranBy = "Test operator" }, ifMatch: etag);
        await Expect(recorded, System.Net.HttpStatusCode.NoContent, "smoke-run");
        var approved = await approver.Send(HttpMethod.Post, $"{url}/smoke-run/approve", ifMatch: recorded.ETag());
        await Expect(approved, System.Net.HttpStatusCode.NoContent, "smoke-run/approve");
        return approved.ETag();
    }

    private static async Task Expect(HttpResponseMessage response, System.Net.HttpStatusCode status, string what)
    {
        if (response.StatusCode != status)
        {
            throw new InvalidOperationException($"{what}: {response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        }
    }

    /// <summary>Everything every readiness check needs, in force from <paramref name="from"/>; <paramref name="staffRole"/> gets its channel rows.</summary>
    /// <summary>
    /// BR-013: two active tenant administrators (a TENANT_ADMIN assignment, the identity linked, signed in once), as the
    /// ORGANISATION_AND_ACCESS step needs. Returns their user IDs; each holds <paramref name="permissions"/> too.
    /// </summary>
    public static async Task<Guid[]> SeedActiveAdministrators(this TestDatabase database, Guid tenantId, string prefix, params string[] permissions)
    {
        var users = new[] { await database.UserWithPermissions(tenantId, prefix, permissions), await database.UserWithPermissions(tenantId, prefix + "-2", permissions) };
        foreach (var user in users)
        {
            await database.ExecuteAsOwner("UPDATE iam.user_role_assignment SET role_code = 'TENANT_ADMIN' WHERE user_id = @u", ("u", user));
            await database.ExecuteAsOwner("UPDATE iam.user_account SET status = 'ACTIVE', last_login_at = now() WHERE id = @u", ("u", user));
        }

        return users;
    }

    public static async Task SeedReadyTenant(this TestDatabase database, Guid tenantId, DateOnly from)
    {
        // PLATFORM_REFERENCE_DATA: global rows (shared by every tenant of the test database).
        foreach (var table in new[] { "refdata.ref_currencies", "refdata.ref_countries", "refdata.ref_languages", "refdata.ref_time_zones" })
        {
            await database.SeedStaticFilled(null, table, "ACTIVE", "ZZ", from);
        }

        // TENANT_REGISTRATION
        await database.SeedStaticFilled(tenantId, "tenant.cfg_locale_defaults", "EFFECTIVE", "LOCALE", from, ("base_currency", "ZAR"), ("languages", new[] { "en" }),
            ("default_language", "en"), ("time_zone", "Africa/Johannesburg"), ("country", "ZA"));

        // ORGANISATION_AND_ACCESS (the first administrator is provisioned by the test; the back-office channel has no
        // staff roles to cover until roles with channel-scoped permissions exist).
        await database.SeedStaticFilled(tenantId, "iam.cfg_roles_and_permission_sets", "EFFECTIVE", "CLERK", from, ("role_code", "CLERK"),
            ("permissions", DBNull.Value));
        await database.GrantFunctionsWithin(tenantId, "CLERK", from, "dead_letter.read");
        await database.SeedStaticFilled(tenantId, "tenant.mst_organisation_structure", "ACTIVE", "ROOT", from, ("node_code", "ROOT"), ("type", "LEGAL_ENTITY"),
            ("parent", DBNull.Value), ("name", "Root"));
        await database.SeedStaticFilled(tenantId, "iam.cfg_authentication_and_session_policies", "EFFECTIVE", "AUTH", from, ("policy_code", "AUTH"),
            ("mfa_required_roles", Array.Empty<string>()), ("session_timeout", TimeSpan.FromMinutes(30)), ("lockout_attempts", 5));
        await database.SeedStaticFilled(tenantId, "channel.cfg_channel_definitions", "EFFECTIVE", "CH-STF", from, ("channel_code", "STF"), ("audience", "STAFF"),
            ("name", "Back office"), ("client_ids", new[] { "insys-web" }));

        // FINANCIAL_FOUNDATIONS
        await database.SeedStaticFilled(tenantId, "finance.mst_chart_of_accounts", "ACTIVE", "ACC-1100", from, ("account_code", "1100"), ("type", "ASSET"), ("status", "ACTIVE"));
        var rule = 0;
        foreach (var type in new[] { "PREMIUM_DUE", "PAYMENT_RECEIVED", "REFUND_PAID", "CLAIM_RESERVE_UP", "CLAIM_RESERVE_DOWN", "CLAIM_PAID", "CLAIM_PAID_REVERSED" })
        {
            await database.SeedStaticFilled(tenantId, "finance.cfg_posting_rules", "EFFECTIVE", $"PR-READY-{++rule}", from, ("rule_code", $"PR-READY-{rule}"),
                ("transaction_type", type), ("debit_account", "1100"), ("credit_account", "1100"));
        }

        await database.ExecuteAsOwner("""
            INSERT INTO finance.accounting_period (tenant_id, id, row_version, created_at, created_by, updated_at, updated_by, period_code, start_date, end_date, status)
            VALUES (@t, gen_random_uuid(), 1, now(), 'seed', now(), 'seed', @p, @s, @e, 'OPEN') ON CONFLICT DO NOTHING
            """, ("t", tenantId), ("p", "READY"), ("s", from), ("e", from.AddYears(5)));

        // SHARED_OPERATIONAL_SETUP
        await database.SeedStaticFilled(tenantId, "workflow.ref_business_calendars", "ACTIVE", "CAL", from);
        await database.SeedStaticFilled(tenantId, "workflow.cfg_task_types_and_work_queues", "EFFECTIVE", "TASKS", from, ("assignment_rule", "MANUAL_PULL"));
        // Cases (Wave 2, OQ-110): a complaint type with its review type, and its ACKNOWLEDGE and RESOLVE timelines (calendar days, so no calendar is named).
        await database.SeedStaticFilled(tenantId, "workflow.cfg_case_types", "EFFECTIVE", "COMPLAINT", from, ("case_type_code", "COMPLAINT"), ("family", "COMPLAINT"),
            ("process_code", "COMPLAINT"), ("link_types", new[] { "POLICY", "CLAIM" }), ("requires_category", false), ("review_case_type_code", "COMPLAINT_REVIEW"),
            ("appeal_route", "PPR_NFO"), ("ombud_scheme_codes", Array.Empty<string>()), ("portal_audiences", Array.Empty<string>()));
        // The process the complaint type names, in force (readiness CASE_TYPE_PROCESS_NOT_IN_FORCE).
        await database.SeedStaticFilled(tenantId, "workflow.cfg_workflow_definitions", "EFFECTIVE", "COMPLAINT", from, ("process_code", "COMPLAINT"),
            ("states", new[] { "HANDLE", "DONE" }), ("transitions", System.Text.Json.JsonDocument.Parse("""[{"from":"HANDLE","to":"DONE"}]""").RootElement), ("domain_validation", ""));
        foreach (var (step, duration) in new[] { ("ACKNOWLEDGE", "P2D"), ("RESOLVE", "P42D") })
        {
            await database.SeedStaticFilled(tenantId, "workflow.cfg_case_timelines", "EFFECTIVE", $"COMPLAINT-{step}", from, ("case_type_code", "COMPLAINT"),
                ("step", step), ("duration", duration), ("basis", "CALENDAR"), ("extension_allowed", false));
        }

        // APPROVAL_AUTHORITY: the clerk decides every approval type a tenant can raise (with static_data.approve for the static data types),
        // with the due diligence types' own permissions, and its back-office channel rows (CHANNELS).
        foreach (var type in BuildingBlocks.Contracts.Approvals.ApprovalKinds.ApprovalTypes)
        {
            await database.SeedStaticFilled(tenantId, "workflow.cfg_delegation_and_approval_authority", "EFFECTIVE", $"AUTH-{type}", from, ("role_code", "CLERK"),
                ("transaction_type", type), ("limit_value", 1_000m), ("delegation_allowed", false));
        }

        string[] approving = ["approval.approve", "static_data.approve", "due_diligence.approve_enhanced", "due_diligence.override_risk", "due_diligence.unblock"];
        await database.GrantFunctionsWithin(tenantId, "CLERK", from, approving);
        await database.ExecuteAsOwner("""
            INSERT INTO channel.cfg_channel_transaction_permissions (tenant_id, code, version, effective_from, record_status, created_by, change_reason,
                                                                     channel_code, role_code, function_code, allowed)
            SELECT @t, 'CTP-CLERK-' || f, 1, @from, 'EFFECTIVE', 'seed', 'test seed', 'STF', 'CLERK', f, true FROM iam.functions_within(@permissions) AS f
            ON CONFLICT DO NOTHING
            """, ("t", tenantId), ("from", from), ("permissions", approving));

        await database.SeedStaticFilled(tenantId, "document.cfg_document_templates", "EFFECTIVE", "TPL-1", from, ("template_code", "TPL-1"), ("language", "en"),
            ("document_type", "POLICY_SCHEDULE"));

        // Due diligence (addenda/due-diligence.md §7): the gates on, a screening and an identity verification partner, and a staff role that reviews hits
        // (with its back-office channel row, CHANNELS).
        await database.SeedStaticFilled(tenantId, "party.cfg_due_diligence_settings", "EFFECTIVE", "GATES-ON", from, ("gates_enabled", true), ("adverse_media_enabled", false));
        foreach (var (partner, type) in new[] { ("SCR-READY", "SCREENING"), ("IDV-READY", "IDENTITY_VERIFICATION") })
        {
            await database.SeedStaticFilled(tenantId, "integration.mst_partner_register", "ACTIVE", partner, from, ("partner_code", partner), ("partner_type", type),
                ("status", "ACTIVE"));
        }

        // ...and that reviews hits and completes periodic reviews (OQ-138).
        await database.GrantFunctionsWithin(tenantId, "CLERK", from, "due_diligence.review_hit", "due_diligence.review");
        await database.SeedStaticFilled(tenantId, "channel.cfg_channel_transaction_permissions", "EFFECTIVE", "CTP-CLERK-DD", from, ("channel_code", "STF"),
            ("role_code", "CLERK"), ("function_code", "PARTY_REVIEW_SCREENING_HIT"), ("allowed", true));
        await database.SeedStaticFilled(tenantId, "channel.cfg_channel_transaction_permissions", "EFFECTIVE", "CTP-CLERK-DD-REVIEW", from, ("channel_code", "STF"),
            ("role_code", "CLERK"), ("function_code", "PARTY_COMPLETE_DUE_DILIGENCE_REVIEW"), ("allowed", true));

        // OQ-110: the case notification types, each with a template.
        foreach (var type in new[] { "CASE_ACKNOWLEDGED", "CASE_UPDATE", "CASE_DECISION" })
        {
            await database.SeedStaticFilled(tenantId, "communication.cfg_notification_types", "EFFECTIVE", $"NT-{type}", from, ("type_code", type), ("category", "SERVICE"),
                ("consent_purpose", DBNull.Value), ("channels", new[] { "EMAIL" }));
            await database.SeedStaticFilled(tenantId, "communication.cfg_notification_templates", "EFFECTIVE", $"NTT-{type}", from, ("type_code", type), ("channel", "EMAIL"),
                ("language", "en"), ("subject", "Case {case_number}"), ("body", "Case {case_number}"));
        }

        // PAYMENTS_AND_DISTRIBUTION
        await database.SeedStaticFilled(tenantId, "billing.ref_collection_methods", "ACTIVE", "DEBIT_ORDER", from, ("method_code", "DEBIT_ORDER"),
            ("payment_method", "DEBIT_ORDER"), ("lead_time", TimeSpan.FromDays(2)));
        await database.SeedStaticFilled(tenantId, "billing.ref_billing_frequencies", "ACTIVE", "MONTHLY", from, ("periods_per_year", 12));
        await database.SeedStaticFilled(tenantId, "billing.cfg_document_numbering_schemes", "EFFECTIVE", "INV", from, ("document_type", "INVOICE"), ("reset_rule", "NEVER"));
        await database.SeedStaticFilled(tenantId, "billing.cfg_arrears_ageing_buckets", "EFFECTIVE", "B1", from, ("bucket_code", "B1"), ("days_from", 1), ("days_to", 30),
            ("action", "REMINDER"));
        await database.SeedStaticFilled(tenantId, "billing.cfg_arrears_ageing_buckets", "EFFECTIVE", "B2", from, ("bucket_code", "B2"), ("days_from", 31), ("days_to", 9999),
            ("action", "LAPSE"));
        foreach (var (method, direction) in new[] { ("COLLECT", "COLLECT"), ("PAY", "PAY") })
        {
            await database.SeedStaticFilled(tenantId, "payment.ref_payment_methods", "ACTIVE", method, from, ("method_code", method), ("direction", direction),
                ("currencies", new[] { "ZAR" }), ("countries", new[] { "ZA" }));
        }

        await database.SeedStaticFilled(tenantId, "payment.mst_payment_provider_and_bank_register", "ACTIVE", "SIMULATOR", from, ("provider_code", "SIMULATOR"),
            ("type", "PAYMENT_GATEWAY"), ("supported_methods", new[] { "COLLECT", "PAY" }));
        await database.SeedStaticFilled(tenantId, "payment.ref_payment_status_mappings", "ACTIVE", "SIM-PAID", from, ("provider_code", "SIMULATOR"),
            ("provider_status", "PAID"), ("platform_status", "SUCCEEDED"), ("final", true));
        await database.SeedStaticFilled(tenantId, "payment.mst_tenant_bank_accounts", "ACTIVE", "BANK", from, ("purpose", "COLLECTIONS"));
        await database.ExecuteAsOwner("""
            INSERT INTO payment.callback_endpoint (tenant_id, id, row_version, created_at, created_by, updated_at, updated_by, provider_code, key_hash, secret_enc, status,
                                                   require_client_token, issued_at, issued_by)
            VALUES (@t, gen_random_uuid(), 1, now(), 'seed', now(), 'seed', 'SIMULATOR', md5(@t::text) || md5(@t::text || 'k'), '\x00', 'ACTIVE', false, now(), 'seed')
            ON CONFLICT DO NOTHING
            """, ("t", tenantId));

        // DOMAIN_REFERENCE_DATA
        await database.SeedStaticFilled(tenantId, "claims.cfg_claim_numbering_schemes", "EFFECTIVE", "CLM", from, ("domain", "GENERAL"), ("check_digit", "NONE"));
        await database.SeedStaticFilled(tenantId, "claims.ref_reserve_types", "ACTIVE", "INDEMNITY", from, ("category", "INDEMNITY"));

        // PRODUCT_FACTORY: a published motor product with a template, available to the tenant, rated at sum insured × 0.03 + 50,
        // with a rating test case that passes.
        await database.ExecuteAsOwner("""
            INSERT INTO product.cfg_product_definitions_and_versions (tenant_id, id, code, version, effective_from, record_status, created_by, change_reason,
                                                                     product_code, domain, name, coverages, family_code, class_code, cover_starts_on)
            VALUES (@t, gen_random_uuid(), 'P-READY', 1, @from, 'PUBLISHED', 'seed', 'published', 'P-READY', 'GENERAL', 'Motor', ARRAY['OWN_DAMAGE'], 'GEN-MOTOR',
                    'MOTOR', 'ISSUE')
            ON CONFLICT DO NOTHING
            """, ("t", tenantId), ("from", from));
        await database.SeedStaticFilled(tenantId, "product.cfg_product_template_assignments", "EFFECTIVE", "P-READY-SCH", from, ("product_code", "P-READY"),
            ("document_type", "POLICY_SCHEDULE"), ("template", "TPL-1"), ("language", "en"));
        await database.SeedStaticFilled(tenantId, "tenant.cfg_tenant_product_availability", "EFFECTIVE", "AVAIL", from, ("product_family", "GEN-MOTOR"),
            ("enabled", true));
        await database.SeedStaticFilled(tenantId, "rating.cfg_rating_factor_definitions", "EFFECTIVE", "VEHICLE_GROUP", from, ("factor_code", "VEHICLE_GROUP"),
            ("data_type", "CODE"), ("source_attribute", "vehicle_group"));
        await database.SeedStaticFilled(tenantId, "rating.cfg_rating_tables", "EFFECTIVE", "PREADY-BASE", from, ("table_code", "PREADY-BASE"),
            ("dimensions", new[] { "VEHICLE_GROUP" }), ("values", System.Text.Json.JsonDocument.Parse("""[{"key":["G3"],"value":0.03}]""").RootElement),
            ("interpolation", "NONE"));
        await database.SeedStaticFilled(tenantId, "rating.cfg_premium_parameters", "EFFECTIVE", "PREADY-P1", from, ("parameter_code", "PREADY-BASE"),
            ("product_version", "P-READY v1"), ("value", 1m), ("unit", "FACTOR"));
        await database.SeedStaticFilled(tenantId, "rating.cfg_premium_parameters", "EFFECTIVE", "PREADY-P2", from, ("parameter_code", "POLICY_FEE"),
            ("product_version", "P-READY v1"), ("value", 50m), ("unit", "AMOUNT"));
        await database.SeedStaticFilled(tenantId, "rating.cfg_rounding_and_currency_precision_rules", "EFFECTIVE", "ZAR-2DP", from, ("rule_code", "ZAR-2DP"),
            ("currency", "ZAR"), ("step", 0.01m), ("method", "HALF_UP"), ("stage", "FINAL_PREMIUM"));
        await database.SeedStaticFilled(tenantId, "rating.cfg_rating_test_case_library", "EFFECTIVE", "PREADY-T1", from, ("case_id", "PREADY-T1"),
            ("product_version", "P-READY v1"),
            ("inputs", System.Text.Json.JsonDocument.Parse("""{"currency":"ZAR","frequency":"ANNUAL","sum_insured":100000,"vehicle_group":"G3"}""").RootElement),
            ("expected_premium", 3050m), ("tolerance", 0m));

        // A role with underwriting authority to decide the product's referrals (GENERAL_REFERRAL_AUTHORITY).
        await database.SeedStaticFilled(tenantId, "rules.cfg_authority_matrices", "EFFECTIVE", "AM-READY", from, ("role_code", "UW-READY"), ("product_code", "P-READY"),
            ("max_sum_assured", 1_000_000m), ("decision_types", new[] { "ACCEPT", "DECLINE" }));

        // INTEGRATION_AND_REPORTING
        await database.SeedStaticFilled(tenantId, "finance.cfg_gl_export_layouts", "EFFECTIVE", "GL", from, ("format", "CSV"));
    }
}
