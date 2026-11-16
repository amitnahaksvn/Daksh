#!/usr/bin/env bash
# Configuration validation: no committed secrets/env files, valid YAML/JSON, solution builds.
set -euo pipefail
cd "$(dirname "$0")/.."
fail=0
if git ls-files | grep -E '(^|/)\.env($|\.)' | grep -v '\.env\.example$'; then echo "ERROR: env file committed"; fail=1; fi
if git ls-files | grep -E '\.(pem|key|pfx|p12|jks|keystore)$'; then echo "ERROR: key material committed"; fail=1; fi
if git grep -nIE '(AKIA[0-9A-Z]{16}|sk-[A-Za-z0-9]{20,}|-----BEGIN [A-Z ]*PRIVATE KEY-----)' -- . ':!scripts/validate.sh' ':!tests/platform/Daksh.Platform.Tests/Security/GuardsTests.cs'; then echo "ERROR: possible secret"; fail=1; fi
python3 - <<'PY' || fail=1
import glob, json, sys
try:
    import yaml
except ImportError:
    yaml = None
ok = True
for f in glob.glob('.github/workflows/*.yml') + ['docker-compose.yml', 'infrastructure/deployment/kubernetes.example.yaml']:
    if yaml:
        try: yaml.safe_load(open(f))
        except Exception as e: print('invalid YAML', f, e); ok = False
for f in ['global.json', '.config/dotnet-tools.json']:
    try: json.load(open(f))
    except Exception as e: print('invalid JSON', f, e); ok = False
sys.exit(0 if ok else 1)
PY
dotnet build Daksh.sln --nologo -v q
exit $fail
