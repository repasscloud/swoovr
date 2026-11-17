"""Pure checks that restore permission repair reviews exactly the routines the code creates (#343); no database."""
import importlib.util
import re
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location('uat_jobs', ROOT / 'scripts/uat/uat_jobs.py')
jobs = importlib.util.module_from_spec(spec)
spec.loader.exec_module(jobs)
TARGET = 'swoovr_restore_' + 'a' * 32
CREATE = re.compile(r'CREATE\s+(?:OR\s+REPLACE\s+)?FUNCTION\s+(?:public\.)?([a-z_][a-z0-9_]*)\s*\((.*?)\bAS\s+\$', re.S | re.I)


def created(paths):
    found = {}
    for path in paths:
        for name, header in CREATE.findall(path.read_text()):
            definer = bool(re.search(r'SECURITY\s+DEFINER', header, re.I))
            if found.setdefault(name, definer) != definer:
                raise AssertionError(f'{name} changes SECURITY DEFINER between definitions')
    return found


class RestoreRoutineChecks(unittest.TestCase):
    def test_api_review_matches_migrations_and_guard_sql(self):
        persistence = ROOT / 'src/Swoovr.Infrastructure/Persistence'
        sources = sorted((persistence / 'Migrations').glob('*.cs')) + sorted(persistence.glob('*GuardSql.cs'))
        reviewed = {name: definer for schema, name, definer in jobs.REVIEWED_ROUTINES['api'] if schema == 'public'}
        self.assertEqual(created(sources), reviewed)

    def test_keycloak_review_matches_contact_policy_provider(self):
        sources = sorted((ROOT / 'containers/keycloak/contact-policy/src/main').rglob('*.xml'))
        reviewed = {name: definer for schema, name, definer in jobs.REVIEWED_ROUTINES['keycloak']}
        self.assertEqual(created(sources), reviewed)

    def test_api_repair_reviews_routines_and_restores_retention_grant(self):
        sql = jobs.restore_permission_sql(TARGET, 'api')
        self.assertIn("('public','swoovr_expire_access_prefix',true)", sql)
        self.assertIn("('public','sensitive_access_log_append_only',false)", sql)
        self.assertIn('Custom routines require explicit ownership review', sql)
        self.assertIn('Overloaded routines require explicit ownership review', sql)
        self.assertIn("ALTER FUNCTION %I.%I(%s) OWNER TO %I',item.nspname,item.proname,item.args,'swoovr_migrator'", sql)
        for name in jobs.RETENTION_ROUTINES:
            self.assertIn(f'REVOKE ALL ON FUNCTION public.{name}(timestamptz) FROM PUBLIC;', sql)
            self.assertIn(f'GRANT EXECUTE ON FUNCTION public.{name}(timestamptz) TO swoovr_api;', sql)

    def test_keycloak_repair_never_grants_api_execute(self):
        sql = jobs.restore_permission_sql(TARGET, 'keycloak')
        self.assertIn("('public','swoovr_retired_contact_guard',false)", sql)
        self.assertIn("'swoovr_keycloak'", sql)
        self.assertNotIn('swoovr_api', sql)

    def test_refuses_unowned_target(self):
        with self.assertRaises(ValueError):
            jobs.restore_permission_sql('swoovr', 'api')


if __name__ == '__main__':
    unittest.main()
