using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Swoovr.Infrastructure.Persistence.Migrations;

// #343: AccessLogRetention revoked EXECUTE from PUBLIC but never granted it to the hosted API role,
// so the retention worker failed with 42501 on UAT. Grant only to swoovr_api, and only where that role
// exists (local single-owner databases have no role split).
[DbContext(typeof(SwoovrDbContext))]
[Migration("20261117000000_RetentionFunctionExecute")]
public sealed class RetentionFunctionExecute:Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)=>migrationBuilder.Sql("""
        REVOKE ALL ON FUNCTION swoovr_expire_access_prefix(timestamptz) FROM PUBLIC;
        REVOKE ALL ON FUNCTION swoovr_expire_operational_metadata(timestamptz) FROM PUBLIC;
        DO $swoovr$ BEGIN
          IF EXISTS(SELECT FROM pg_roles WHERE rolname='swoovr_api') THEN
            GRANT EXECUTE ON FUNCTION swoovr_expire_access_prefix(timestamptz) TO swoovr_api;
            GRANT EXECUTE ON FUNCTION swoovr_expire_operational_metadata(timestamptz) TO swoovr_api;
          END IF;
        END $swoovr$;
        """);
    protected override void Down(MigrationBuilder migrationBuilder)=>migrationBuilder.Sql("""
        DO $swoovr$ BEGIN
          IF EXISTS(SELECT FROM pg_roles WHERE rolname='swoovr_api') THEN
            REVOKE EXECUTE ON FUNCTION swoovr_expire_access_prefix(timestamptz) FROM swoovr_api;
            REVOKE EXECUTE ON FUNCTION swoovr_expire_operational_metadata(timestamptz) FROM swoovr_api;
          END IF;
        END $swoovr$;
        """);
}
