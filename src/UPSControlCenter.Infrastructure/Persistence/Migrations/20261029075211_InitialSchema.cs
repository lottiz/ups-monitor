using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace UPSControlCenter.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "ups");

            migrationBuilder.CreateTable(
                name: "AspNetRoles",
                schema: "ups",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    normalized_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    concurrency_stamp = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_asp_net_roles", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUsers",
                schema: "ups",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    full_name = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_login_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    user_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    normalized_user_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    normalized_email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    email_confirmed = table.Column<bool>(type: "boolean", nullable: false),
                    password_hash = table.Column<string>(type: "text", nullable: true),
                    security_stamp = table.Column<string>(type: "text", nullable: true),
                    concurrency_stamp = table.Column<string>(type: "text", nullable: true),
                    phone_number = table.Column<string>(type: "text", nullable: true),
                    phone_number_confirmed = table.Column<bool>(type: "boolean", nullable: false),
                    two_factor_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    lockout_end = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    lockout_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    access_failed_count = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_asp_net_users", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "audit_logs",
                schema: "ups",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    user_id = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true),
                    user_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ip_address = table.Column<string>(type: "character varying(45)", maxLength: 45, nullable: true),
                    action = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    entity_type = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    entity_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    before_json = table.Column<string>(type: "jsonb", nullable: true),
                    after_json = table.Column<string>(type: "jsonb", nullable: true),
                    outcome = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    reason = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_audit_logs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "configuration_versions",
                schema: "ups",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    payload_json = table.Column<string>(type: "jsonb", nullable: false),
                    checksum_sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    signature = table.Column<byte[]>(type: "bytea", nullable: false),
                    state = table.Column<int>(type: "integer", nullable: false),
                    created_by = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    acknowledged_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    activated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    activated_by_agent = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    validation_errors_json = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_configuration_versions", x => x.id);
                    table.CheckConstraint("ck_configuration_versions_version", "version > 0");
                });

            migrationBuilder.CreateTable(
                name: "connection_profiles",
                schema: "ups",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    description = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    auth_type = table.Column<int>(type: "integer", nullable: false),
                    username = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    secret_ciphertext = table.Column<byte[]>(type: "bytea", nullable: true),
                    secret_key_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    fingerprint = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    is_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_connection_profiles", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "device_groups",
                schema: "ups",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    description = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    priority = table.Column<int>(type: "integer", nullable: false),
                    shutdown_order = table.Column<int>(type: "integer", nullable: false),
                    is_enabled = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_device_groups", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "notification_channels",
                schema: "ups",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    channel_type = table.Column<int>(type: "integer", nullable: false),
                    config_json = table.Column<string>(type: "jsonb", nullable: false),
                    secret_ciphertext = table.Column<byte[]>(type: "bytea", nullable: true),
                    is_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    cooldown_seconds = table.Column<int>(type: "integer", nullable: false),
                    min_severity = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_notification_channels", x => x.id);
                    table.CheckConstraint("ck_notification_channels_cooldown", "cooldown_seconds BETWEEN 0 AND 86400");
                });

            migrationBuilder.CreateTable(
                name: "orchestration_runs",
                schema: "ups",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    blackout_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    run_kind = table.Column<int>(type: "integer", nullable: false),
                    trigger_reason = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    execution_mode = table.Column<int>(type: "integer", nullable: false),
                    sequence_id = table.Column<Guid>(type: "uuid", nullable: true),
                    state = table.Column<int>(type: "integer", nullable: false),
                    owner = table.Column<int>(type: "integer", nullable: false),
                    fencing_token = table.Column<long>(type: "bigint", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    rule_snapshot_json = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_orchestration_runs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "shutdown_sequences",
                schema: "ups",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    description = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    is_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_shutdown_sequences", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "simulation_runs",
                schema: "ups",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    scenario_json = table.Column<string>(type: "jsonb", nullable: false),
                    timeline_json = table.Column<string>(type: "jsonb", nullable: true),
                    state = table.Column<int>(type: "integer", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_by = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    simulated_duration_seconds = table.Column<int>(type: "integer", nullable: false),
                    summary = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_simulation_runs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "startup_sequences",
                schema: "ups",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    description = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    is_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_startup_sequences", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "system_settings",
                schema: "ups",
                columns: table => new
                {
                    key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    value = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: true),
                    value_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    description = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    is_sensitive = table.Column<bool>(type: "boolean", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_system_settings", x => x.key);
                });

            migrationBuilder.CreateTable(
                name: "ups_devices",
                schema: "ups",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    description = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    nut_host = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    nut_port = table.Column<int>(type: "integer", nullable: false),
                    nut_ups_name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    polling_interval_seconds = table.Column<int>(type: "integer", nullable: false),
                    communication_timeout_seconds = table.Column<int>(type: "integer", nullable: false),
                    stale_data_threshold_seconds = table.Column<int>(type: "integer", nullable: false),
                    manufacturer = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    model = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    serial = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    firmware_version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    is_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ups_devices", x => x.id);
                    table.CheckConstraint("ck_ups_devices_polling", "polling_interval_seconds BETWEEN 2 AND 300");
                    table.CheckConstraint("ck_ups_devices_port", "nut_port BETWEEN 1 AND 65535");
                    table.CheckConstraint("ck_ups_devices_stale", "stale_data_threshold_seconds > polling_interval_seconds");
                    table.CheckConstraint("ck_ups_devices_timeout", "communication_timeout_seconds BETWEEN 5 AND 600");
                });

            migrationBuilder.CreateTable(
                name: "worker_heartbeats",
                schema: "ups",
                columns: table => new
                {
                    worker_name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    instance_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    state = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    fencing_token = table.Column<long>(type: "bigint", nullable: false),
                    details_json = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_worker_heartbeats", x => x.worker_name);
                });

            migrationBuilder.CreateTable(
                name: "AspNetRoleClaims",
                schema: "ups",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    role_id = table.Column<string>(type: "text", nullable: false),
                    claim_type = table.Column<string>(type: "text", nullable: true),
                    claim_value = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_asp_net_role_claims", x => x.id);
                    table.ForeignKey(
                        name: "fk_asp_net_role_claims_asp_net_roles_role_id",
                        column: x => x.role_id,
                        principalSchema: "ups",
                        principalTable: "AspNetRoles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserClaims",
                schema: "ups",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    user_id = table.Column<string>(type: "text", nullable: false),
                    claim_type = table.Column<string>(type: "text", nullable: true),
                    claim_value = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_asp_net_user_claims", x => x.id);
                    table.ForeignKey(
                        name: "fk_asp_net_user_claims_asp_net_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "ups",
                        principalTable: "AspNetUsers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserLogins",
                schema: "ups",
                columns: table => new
                {
                    login_provider = table.Column<string>(type: "text", nullable: false),
                    provider_key = table.Column<string>(type: "text", nullable: false),
                    provider_display_name = table.Column<string>(type: "text", nullable: true),
                    user_id = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_asp_net_user_logins", x => new { x.login_provider, x.provider_key });
                    table.ForeignKey(
                        name: "fk_asp_net_user_logins_asp_net_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "ups",
                        principalTable: "AspNetUsers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserRoles",
                schema: "ups",
                columns: table => new
                {
                    user_id = table.Column<string>(type: "text", nullable: false),
                    role_id = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_asp_net_user_roles", x => new { x.user_id, x.role_id });
                    table.ForeignKey(
                        name: "fk_asp_net_user_roles_asp_net_roles_role_id",
                        column: x => x.role_id,
                        principalSchema: "ups",
                        principalTable: "AspNetRoles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_asp_net_user_roles_asp_net_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "ups",
                        principalTable: "AspNetUsers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserTokens",
                schema: "ups",
                columns: table => new
                {
                    user_id = table.Column<string>(type: "text", nullable: false),
                    login_provider = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    value = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_asp_net_user_tokens", x => new { x.user_id, x.login_provider, x.name });
                    table.ForeignKey(
                        name: "fk_asp_net_user_tokens_asp_net_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "ups",
                        principalTable: "AspNetUsers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "managed_devices",
                schema: "ups",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    description = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    hostname = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    ip_address = table.Column<string>(type: "character varying(45)", maxLength: 45, nullable: true),
                    operating_system = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    device_type = table.Column<int>(type: "integer", nullable: false),
                    device_group_id = table.Column<Guid>(type: "uuid", nullable: true),
                    priority = table.Column<int>(type: "integer", nullable: false),
                    shutdown_protocol = table.Column<int>(type: "integer", nullable: false),
                    startup_protocol = table.Column<int>(type: "integer", nullable: false),
                    management_port = table.Column<int>(type: "integer", nullable: true),
                    connection_profile_id = table.Column<Guid>(type: "uuid", nullable: true),
                    mac_address = table.Column<string>(type: "character varying(17)", maxLength: 17, nullable: true),
                    shutdown_timeout_seconds = table.Column<int>(type: "integer", nullable: false),
                    startup_timeout_seconds = table.Column<int>(type: "integer", nullable: false),
                    post_action_delay_seconds = table.Column<int>(type: "integer", nullable: false),
                    status_check_method = table.Column<int>(type: "integer", nullable: false),
                    status_check_target = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    is_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    notes = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    last_known_state = table.Column<int>(type: "integer", nullable: false),
                    last_state_change_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    startup_capability_verified = table.Column<bool>(type: "boolean", nullable: true),
                    capability_verified_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_managed_devices", x => x.id);
                    table.CheckConstraint("ck_managed_devices_port", "management_port IS NULL OR management_port BETWEEN 1 AND 65535");
                    table.CheckConstraint("ck_managed_devices_priority", "priority BETWEEN 1 AND 1000");
                    table.CheckConstraint("ck_managed_devices_shutdown_timeout", "shutdown_timeout_seconds BETWEEN 10 AND 3600");
                    table.CheckConstraint("ck_managed_devices_startup_timeout", "startup_timeout_seconds BETWEEN 10 AND 7200");
                    table.ForeignKey(
                        name: "fk_managed_devices_connection_profiles_connection_profile_id",
                        column: x => x.connection_profile_id,
                        principalSchema: "ups",
                        principalTable: "connection_profiles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_managed_devices_device_groups_device_group_id",
                        column: x => x.device_group_id,
                        principalSchema: "ups",
                        principalTable: "device_groups",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "notification_events",
                schema: "ups",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    notification_channel_id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_type = table.Column<int>(type: "integer", nullable: false),
                    dedup_key = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    state = table.Column<int>(type: "integer", nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    error = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    payload_json = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_notification_events", x => x.id);
                    table.ForeignKey(
                        name: "fk_notification_events_notification_channels_notification_chan",
                        column: x => x.notification_channel_id,
                        principalSchema: "ups",
                        principalTable: "notification_channels",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "orchestration_actions",
                schema: "ups",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    orchestration_run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    step_id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_id = table.Column<Guid>(type: "uuid", nullable: true),
                    attempt = table.Column<int>(type: "integer", nullable: false),
                    action_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    state = table.Column<int>(type: "integer", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    result_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    message = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    adapter_name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    was_dry_run = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_orchestration_actions", x => x.id);
                    table.CheckConstraint("ck_orchestration_actions_attempt", "attempt BETWEEN 1 AND 10");
                    table.ForeignKey(
                        name: "fk_orchestration_actions_orchestration_runs_orchestration_run_",
                        column: x => x.orchestration_run_id,
                        principalSchema: "ups",
                        principalTable: "orchestration_runs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "shutdown_policies",
                schema: "ups",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    description = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    execution_mode = table.Column<int>(type: "integer", nullable: false),
                    max_blackout_duration_seconds = table.Column<int>(type: "integer", nullable: false),
                    min_remaining_runtime_seconds = table.Column<int>(type: "integer", nullable: false),
                    debounce_samples = table.Column<int>(type: "integer", nullable: false),
                    unknown_runtime_policy = table.Column<int>(type: "integer", nullable: false),
                    uncertain_duration_policy = table.Column<int>(type: "integer", nullable: false),
                    power_restored_during_shutdown_policy = table.Column<int>(type: "integer", nullable: false),
                    communication_lost_policy = table.Column<int>(type: "integer", nullable: false),
                    communication_lost_grace_seconds = table.Column<int>(type: "integer", nullable: false),
                    critical_battery_charge_percent = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: true),
                    takeover_grace_seconds = table.Column<int>(type: "integer", nullable: false),
                    default_sequence_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_shutdown_policies", x => x.id);
                    table.CheckConstraint("ck_shutdown_policies_critical_charge", "critical_battery_charge_percent IS NULL OR critical_battery_charge_percent BETWEEN 0 AND 100");
                    table.CheckConstraint("ck_shutdown_policies_debounce", "debounce_samples BETWEEN 1 AND 60");
                    table.CheckConstraint("ck_shutdown_policies_max_blackout", "max_blackout_duration_seconds BETWEEN 30 AND 86400");
                    table.CheckConstraint("ck_shutdown_policies_min_runtime", "min_remaining_runtime_seconds BETWEEN 60 AND 86400");
                    table.CheckConstraint("ck_shutdown_policies_takeover", "takeover_grace_seconds BETWEEN 30 AND 3600");
                    table.ForeignKey(
                        name: "fk_shutdown_policies_shutdown_sequences_default_sequence_id",
                        column: x => x.default_sequence_id,
                        principalSchema: "ups",
                        principalTable: "shutdown_sequences",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "startup_policies",
                schema: "ups",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    description = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    execution_mode = table.Column<int>(type: "integer", nullable: false),
                    required_stable_power_seconds = table.Column<int>(type: "integer", nullable: false),
                    stability_samples = table.Column<int>(type: "integer", nullable: false),
                    min_battery_charge_percent = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    anti_flapping_cooldown_seconds = table.Column<int>(type: "integer", nullable: false),
                    require_manual_approval = table.Column<bool>(type: "boolean", nullable: false),
                    default_sequence_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_startup_policies", x => x.id);
                    table.CheckConstraint("ck_startup_policies_charge", "min_battery_charge_percent BETWEEN 0 AND 100");
                    table.CheckConstraint("ck_startup_policies_samples", "stability_samples BETWEEN 2 AND 1000");
                    table.CheckConstraint("ck_startup_policies_stable_power", "required_stable_power_seconds BETWEEN 30 AND 86400");
                    table.ForeignKey(
                        name: "fk_startup_policies_startup_sequences_default_sequence_id",
                        column: x => x.default_sequence_id,
                        principalSchema: "ups",
                        principalTable: "startup_sequences",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ups_events",
                schema: "ups",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ups_device_id = table.Column<Guid>(type: "uuid", nullable: true),
                    event_type = table.Column<int>(type: "integer", nullable: false),
                    severity = table.Column<int>(type: "integer", nullable: false),
                    blackout_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    previous_status = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    new_status = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    details_json = table.Column<string>(type: "jsonb", nullable: true),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    detected_by = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ups_events", x => x.id);
                    table.ForeignKey(
                        name: "fk_ups_events_ups_devices_ups_device_id",
                        column: x => x.ups_device_id,
                        principalSchema: "ups",
                        principalTable: "ups_devices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "ups_measurements",
                schema: "ups",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ups_device_id = table.Column<Guid>(type: "uuid", nullable: false),
                    recorded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    status_raw = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    status_flags = table.Column<int>(type: "integer", nullable: false),
                    battery_charge_percent = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: true),
                    battery_runtime_seconds = table.Column<int>(type: "integer", nullable: true),
                    battery_voltage = table.Column<decimal>(type: "numeric(8,2)", precision: 8, scale: 2, nullable: true),
                    input_voltage = table.Column<decimal>(type: "numeric(8,2)", precision: 8, scale: 2, nullable: true),
                    output_voltage = table.Column<decimal>(type: "numeric(8,2)", precision: 8, scale: 2, nullable: true),
                    input_frequency = table.Column<decimal>(type: "numeric(6,2)", precision: 6, scale: 2, nullable: true),
                    output_frequency = table.Column<decimal>(type: "numeric(6,2)", precision: 6, scale: 2, nullable: true),
                    load_percent = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: true),
                    real_power_watts = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: true),
                    phase_data_json = table.Column<string>(type: "jsonb", nullable: true),
                    is_stale = table.Column<bool>(type: "boolean", nullable: false),
                    source = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ups_measurements", x => x.id);
                    table.ForeignKey(
                        name: "fk_ups_measurements_ups_devices_ups_device_id",
                        column: x => x.ups_device_id,
                        principalSchema: "ups",
                        principalTable: "ups_devices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "device_dependencies",
                schema: "ups",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    dependent_device_id = table.Column<Guid>(type: "uuid", nullable: false),
                    depends_on_device_id = table.Column<Guid>(type: "uuid", nullable: false),
                    dependency_type = table.Column<int>(type: "integer", nullable: false),
                    is_hard = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_device_dependencies", x => x.id);
                    table.CheckConstraint("ck_device_dependencies_no_self", "dependent_device_id <> depends_on_device_id");
                    table.ForeignKey(
                        name: "fk_device_dependencies_managed_devices_dependent_device_id",
                        column: x => x.dependent_device_id,
                        principalSchema: "ups",
                        principalTable: "managed_devices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_device_dependencies_managed_devices_depends_on_device_id",
                        column: x => x.depends_on_device_id,
                        principalSchema: "ups",
                        principalTable: "managed_devices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "device_power_sources",
                schema: "ups",
                columns: table => new
                {
                    managed_device_id = table.Column<Guid>(type: "uuid", nullable: false),
                    ups_device_id = table.Column<Guid>(type: "uuid", nullable: false),
                    is_primary = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_device_power_sources", x => new { x.managed_device_id, x.ups_device_id });
                    table.ForeignKey(
                        name: "fk_device_power_sources_managed_devices_managed_device_id",
                        column: x => x.managed_device_id,
                        principalSchema: "ups",
                        principalTable: "managed_devices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_device_power_sources_ups_devices_ups_device_id",
                        column: x => x.ups_device_id,
                        principalSchema: "ups",
                        principalTable: "ups_devices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "shutdown_steps",
                schema: "ups",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    shutdown_sequence_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_index = table.Column<int>(type: "integer", nullable: false),
                    name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    step_type = table.Column<int>(type: "integer", nullable: false),
                    target_device_id = table.Column<Guid>(type: "uuid", nullable: true),
                    target_group_id = table.Column<Guid>(type: "uuid", nullable: true),
                    parallel_group = table.Column<int>(type: "integer", nullable: true),
                    delay_before_seconds = table.Column<int>(type: "integer", nullable: false),
                    timeout_seconds = table.Column<int>(type: "integer", nullable: false),
                    max_retries = table.Column<int>(type: "integer", nullable: false),
                    continue_on_error = table.Column<bool>(type: "boolean", nullable: false),
                    verify_completion = table.Column<bool>(type: "boolean", nullable: false),
                    condition_json = table.Column<string>(type: "jsonb", nullable: true),
                    excluded_device_ids = table.Column<List<Guid>>(type: "uuid[]", nullable: false),
                    is_enabled = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_shutdown_steps", x => x.id);
                    table.CheckConstraint("ck_shutdown_steps_delay", "delay_before_seconds BETWEEN 0 AND 3600");
                    table.CheckConstraint("ck_shutdown_steps_retries", "max_retries BETWEEN 0 AND 5");
                    table.CheckConstraint("ck_shutdown_steps_timeout", "timeout_seconds BETWEEN 5 AND 7200");
                    table.ForeignKey(
                        name: "fk_shutdown_steps_device_groups_target_group_id",
                        column: x => x.target_group_id,
                        principalSchema: "ups",
                        principalTable: "device_groups",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_shutdown_steps_managed_devices_target_device_id",
                        column: x => x.target_device_id,
                        principalSchema: "ups",
                        principalTable: "managed_devices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_shutdown_steps_shutdown_sequences_shutdown_sequence_id",
                        column: x => x.shutdown_sequence_id,
                        principalSchema: "ups",
                        principalTable: "shutdown_sequences",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "startup_steps",
                schema: "ups",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    startup_sequence_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_index = table.Column<int>(type: "integer", nullable: false),
                    name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    step_type = table.Column<int>(type: "integer", nullable: false),
                    target_device_id = table.Column<Guid>(type: "uuid", nullable: true),
                    target_group_id = table.Column<Guid>(type: "uuid", nullable: true),
                    parallel_group = table.Column<int>(type: "integer", nullable: true),
                    delay_before_seconds = table.Column<int>(type: "integer", nullable: false),
                    timeout_seconds = table.Column<int>(type: "integer", nullable: false),
                    max_retries = table.Column<int>(type: "integer", nullable: false),
                    continue_on_error = table.Column<bool>(type: "boolean", nullable: false),
                    verify_completion = table.Column<bool>(type: "boolean", nullable: false),
                    condition_json = table.Column<string>(type: "jsonb", nullable: true),
                    excluded_device_ids = table.Column<List<Guid>>(type: "uuid[]", nullable: false),
                    is_enabled = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_startup_steps", x => x.id);
                    table.CheckConstraint("ck_startup_steps_retries", "max_retries BETWEEN 0 AND 5");
                    table.CheckConstraint("ck_startup_steps_timeout", "timeout_seconds BETWEEN 5 AND 7200");
                    table.ForeignKey(
                        name: "fk_startup_steps_device_groups_target_group_id",
                        column: x => x.target_group_id,
                        principalSchema: "ups",
                        principalTable: "device_groups",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_startup_steps_managed_devices_target_device_id",
                        column: x => x.target_device_id,
                        principalSchema: "ups",
                        principalTable: "managed_devices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_startup_steps_startup_sequences_startup_sequence_id",
                        column: x => x.startup_sequence_id,
                        principalSchema: "ups",
                        principalTable: "startup_sequences",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "shutdown_rules",
                schema: "ups",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    shutdown_policy_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    description = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    condition_tree_json = table.Column<string>(type: "jsonb", nullable: false),
                    priority = table.Column<int>(type: "integer", nullable: false),
                    is_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    trigger_sequence_id = table.Column<Guid>(type: "uuid", nullable: true),
                    time_windows_json = table.Column<string>(type: "jsonb", nullable: true),
                    days_of_week_mask = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_shutdown_rules", x => x.id);
                    table.ForeignKey(
                        name: "fk_shutdown_rules_shutdown_policies_shutdown_policy_id",
                        column: x => x.shutdown_policy_id,
                        principalSchema: "ups",
                        principalTable: "shutdown_policies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_shutdown_rules_shutdown_sequences_trigger_sequence_id",
                        column: x => x.trigger_sequence_id,
                        principalSchema: "ups",
                        principalTable: "shutdown_sequences",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_asp_net_role_claims_role_id",
                schema: "ups",
                table: "AspNetRoleClaims",
                column: "role_id");

            migrationBuilder.CreateIndex(
                name: "RoleNameIndex",
                schema: "ups",
                table: "AspNetRoles",
                column: "normalized_name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_asp_net_user_claims_user_id",
                schema: "ups",
                table: "AspNetUserClaims",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_asp_net_user_logins_user_id",
                schema: "ups",
                table: "AspNetUserLogins",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_asp_net_user_roles_role_id",
                schema: "ups",
                table: "AspNetUserRoles",
                column: "role_id");

            migrationBuilder.CreateIndex(
                name: "EmailIndex",
                schema: "ups",
                table: "AspNetUsers",
                column: "normalized_email");

            migrationBuilder.CreateIndex(
                name: "UserNameIndex",
                schema: "ups",
                table: "AspNetUsers",
                column: "normalized_user_name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_audit_logs_entity_type_entity_id",
                schema: "ups",
                table: "audit_logs",
                columns: new[] { "entity_type", "entity_id" });

            migrationBuilder.CreateIndex(
                name: "ix_audit_logs_occurred_at",
                schema: "ups",
                table: "audit_logs",
                column: "occurred_at",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "ix_audit_logs_user_id",
                schema: "ups",
                table: "audit_logs",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_configuration_versions_version",
                schema: "ups",
                table: "configuration_versions",
                column: "version",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_configuration_versions_single_published",
                schema: "ups",
                table: "configuration_versions",
                column: "state",
                unique: true,
                filter: "state = 2");

            migrationBuilder.CreateIndex(
                name: "ix_connection_profiles_name",
                schema: "ups",
                table: "connection_profiles",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_device_dependencies_dependent_device_id_depends_on_device_id",
                schema: "ups",
                table: "device_dependencies",
                columns: new[] { "dependent_device_id", "depends_on_device_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_device_dependencies_depends_on_device_id",
                schema: "ups",
                table: "device_dependencies",
                column: "depends_on_device_id");

            migrationBuilder.CreateIndex(
                name: "ix_device_groups_name",
                schema: "ups",
                table: "device_groups",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_device_power_sources_ups_device_id",
                schema: "ups",
                table: "device_power_sources",
                column: "ups_device_id");

            migrationBuilder.CreateIndex(
                name: "ix_managed_devices_connection_profile_id",
                schema: "ups",
                table: "managed_devices",
                column: "connection_profile_id");

            migrationBuilder.CreateIndex(
                name: "ix_managed_devices_device_group_id",
                schema: "ups",
                table: "managed_devices",
                column: "device_group_id");

            migrationBuilder.CreateIndex(
                name: "ix_managed_devices_is_enabled",
                schema: "ups",
                table: "managed_devices",
                column: "is_enabled");

            migrationBuilder.CreateIndex(
                name: "ix_managed_devices_name",
                schema: "ups",
                table: "managed_devices",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_managed_devices_priority",
                schema: "ups",
                table: "managed_devices",
                column: "priority");

            migrationBuilder.CreateIndex(
                name: "ix_notification_channels_name",
                schema: "ups",
                table: "notification_channels",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_notification_events_notification_channel_id_dedup_key_creat",
                schema: "ups",
                table: "notification_events",
                columns: new[] { "notification_channel_id", "dedup_key", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_notification_events_state",
                schema: "ups",
                table: "notification_events",
                column: "state");

            migrationBuilder.CreateIndex(
                name: "ux_orchestration_actions_logical",
                schema: "ups",
                table: "orchestration_actions",
                columns: new[] { "orchestration_run_id", "step_id", "device_id", "attempt" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_orchestration_runs_started_at",
                schema: "ups",
                table: "orchestration_runs",
                column: "started_at",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "ix_orchestration_runs_state",
                schema: "ups",
                table: "orchestration_runs",
                column: "state");

            migrationBuilder.CreateIndex(
                name: "ux_orchestration_runs_active_per_blackout",
                schema: "ups",
                table: "orchestration_runs",
                columns: new[] { "blackout_id", "run_kind" },
                unique: true,
                filter: "state IN (0, 1) AND blackout_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_shutdown_policies_default_sequence_id",
                schema: "ups",
                table: "shutdown_policies",
                column: "default_sequence_id");

            migrationBuilder.CreateIndex(
                name: "ix_shutdown_policies_name",
                schema: "ups",
                table: "shutdown_policies",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_shutdown_policies_single_active",
                schema: "ups",
                table: "shutdown_policies",
                column: "is_active",
                unique: true,
                filter: "is_active");

            migrationBuilder.CreateIndex(
                name: "ix_shutdown_rules_shutdown_policy_id_name",
                schema: "ups",
                table: "shutdown_rules",
                columns: new[] { "shutdown_policy_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_shutdown_rules_shutdown_policy_id_priority",
                schema: "ups",
                table: "shutdown_rules",
                columns: new[] { "shutdown_policy_id", "priority" });

            migrationBuilder.CreateIndex(
                name: "ix_shutdown_rules_trigger_sequence_id",
                schema: "ups",
                table: "shutdown_rules",
                column: "trigger_sequence_id");

            migrationBuilder.CreateIndex(
                name: "ix_shutdown_sequences_name",
                schema: "ups",
                table: "shutdown_sequences",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_shutdown_steps_shutdown_sequence_id_order_index",
                schema: "ups",
                table: "shutdown_steps",
                columns: new[] { "shutdown_sequence_id", "order_index" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_shutdown_steps_target_device_id",
                schema: "ups",
                table: "shutdown_steps",
                column: "target_device_id");

            migrationBuilder.CreateIndex(
                name: "ix_shutdown_steps_target_group_id",
                schema: "ups",
                table: "shutdown_steps",
                column: "target_group_id");

            migrationBuilder.CreateIndex(
                name: "ix_simulation_runs_started_at",
                schema: "ups",
                table: "simulation_runs",
                column: "started_at",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "ix_startup_policies_default_sequence_id",
                schema: "ups",
                table: "startup_policies",
                column: "default_sequence_id");

            migrationBuilder.CreateIndex(
                name: "ix_startup_policies_name",
                schema: "ups",
                table: "startup_policies",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_startup_policies_single_active",
                schema: "ups",
                table: "startup_policies",
                column: "is_active",
                unique: true,
                filter: "is_active");

            migrationBuilder.CreateIndex(
                name: "ix_startup_sequences_name",
                schema: "ups",
                table: "startup_sequences",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_startup_steps_startup_sequence_id_order_index",
                schema: "ups",
                table: "startup_steps",
                columns: new[] { "startup_sequence_id", "order_index" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_startup_steps_target_device_id",
                schema: "ups",
                table: "startup_steps",
                column: "target_device_id");

            migrationBuilder.CreateIndex(
                name: "ix_startup_steps_target_group_id",
                schema: "ups",
                table: "startup_steps",
                column: "target_group_id");

            migrationBuilder.CreateIndex(
                name: "ix_ups_devices_name",
                schema: "ups",
                table: "ups_devices",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_ups_devices_nut_host_nut_port_nut_ups_name",
                schema: "ups",
                table: "ups_devices",
                columns: new[] { "nut_host", "nut_port", "nut_ups_name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_ups_events_blackout_id",
                schema: "ups",
                table: "ups_events",
                column: "blackout_id");

            migrationBuilder.CreateIndex(
                name: "ix_ups_events_event_type_occurred_at",
                schema: "ups",
                table: "ups_events",
                columns: new[] { "event_type", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "ix_ups_events_occurred_at",
                schema: "ups",
                table: "ups_events",
                column: "occurred_at",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "ix_ups_events_ups_device_id",
                schema: "ups",
                table: "ups_events",
                column: "ups_device_id");

            migrationBuilder.CreateIndex(
                name: "ix_ups_measurements_device_recorded",
                schema: "ups",
                table: "ups_measurements",
                columns: new[] { "ups_device_id", "recorded_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_ups_measurements_recorded_brin",
                schema: "ups",
                table: "ups_measurements",
                column: "recorded_at")
                .Annotation("Npgsql:IndexMethod", "brin");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AspNetRoleClaims",
                schema: "ups");

            migrationBuilder.DropTable(
                name: "AspNetUserClaims",
                schema: "ups");

            migrationBuilder.DropTable(
                name: "AspNetUserLogins",
                schema: "ups");

            migrationBuilder.DropTable(
                name: "AspNetUserRoles",
                schema: "ups");

            migrationBuilder.DropTable(
                name: "AspNetUserTokens",
                schema: "ups");

            migrationBuilder.DropTable(
                name: "audit_logs",
                schema: "ups");

            migrationBuilder.DropTable(
                name: "configuration_versions",
                schema: "ups");

            migrationBuilder.DropTable(
                name: "device_dependencies",
                schema: "ups");

            migrationBuilder.DropTable(
                name: "device_power_sources",
                schema: "ups");

            migrationBuilder.DropTable(
                name: "notification_events",
                schema: "ups");

            migrationBuilder.DropTable(
                name: "orchestration_actions",
                schema: "ups");

            migrationBuilder.DropTable(
                name: "shutdown_rules",
                schema: "ups");

            migrationBuilder.DropTable(
                name: "shutdown_steps",
                schema: "ups");

            migrationBuilder.DropTable(
                name: "simulation_runs",
                schema: "ups");

            migrationBuilder.DropTable(
                name: "startup_policies",
                schema: "ups");

            migrationBuilder.DropTable(
                name: "startup_steps",
                schema: "ups");

            migrationBuilder.DropTable(
                name: "system_settings",
                schema: "ups");

            migrationBuilder.DropTable(
                name: "ups_events",
                schema: "ups");

            migrationBuilder.DropTable(
                name: "ups_measurements",
                schema: "ups");

            migrationBuilder.DropTable(
                name: "worker_heartbeats",
                schema: "ups");

            migrationBuilder.DropTable(
                name: "AspNetRoles",
                schema: "ups");

            migrationBuilder.DropTable(
                name: "AspNetUsers",
                schema: "ups");

            migrationBuilder.DropTable(
                name: "notification_channels",
                schema: "ups");

            migrationBuilder.DropTable(
                name: "orchestration_runs",
                schema: "ups");

            migrationBuilder.DropTable(
                name: "shutdown_policies",
                schema: "ups");

            migrationBuilder.DropTable(
                name: "managed_devices",
                schema: "ups");

            migrationBuilder.DropTable(
                name: "startup_sequences",
                schema: "ups");

            migrationBuilder.DropTable(
                name: "ups_devices",
                schema: "ups");

            migrationBuilder.DropTable(
                name: "shutdown_sequences",
                schema: "ups");

            migrationBuilder.DropTable(
                name: "connection_profiles",
                schema: "ups");

            migrationBuilder.DropTable(
                name: "device_groups",
                schema: "ups");
        }
    }
}
