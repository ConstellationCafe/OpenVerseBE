#!/usr/bin/env bash
# Turns the TRX results into workflow annotations (readable from the API without the raw log): counts, and each failed
# test with the first lines of its error message. Fails when nothing ran, or when a test fails that is not listed in
# the known-failures file (second argument, optional)
set -euo pipefail
exec python3 - "${1:-TestResults}" "${2:-}" <<'PY'
import glob, os, sys, xml.etree.ElementTree as ET
d, known_file = sys.argv[1], sys.argv[2]
known = set()
if known_file and os.path.exists(known_file):
    known = {l.strip() for l in open(known_file) if l.strip() and not l.startswith('#')}
ns = {'t': 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
total = passed = failed = skipped = unexpected = 0
for f in glob.glob(os.path.join(d, '**', '*.trx'), recursive=True):
    root = ET.parse(f).getroot()
    c = root.find('.//t:ResultSummary/t:Counters', ns)
    if c is not None:
        total += int(c.get('total', 0)); passed += int(c.get('passed', 0))
        failed += int(c.get('failed', 0)); skipped += int(c.get('notExecuted', 0))
    for r in root.iterfind('.//t:UnitTestResult', ns):
        if r.get('outcome') != 'Failed':
            continue
        name = r.get('testName', '?')
        msg = (r.findtext('.//t:ErrorInfo/t:Message', default='', namespaces=ns) or '').strip()
        trace = (r.findtext('.//t:ErrorInfo/t:StackTrace', default='', namespaces=ns) or '').strip().splitlines()
        detail = ' | '.join([msg.replace('\n', ' ')[:600]] + [l.strip() for l in trace[:3]])
        if name in known:
            print(f'::warning title=known failure::{name}')
        else:
            unexpected += 1
            print(f'::error title=failed test::{name} :: {detail}')
print(f'::notice title=tests::total={total} passed={passed} failed={failed} (unexpected={unexpected}) skipped={skipped}')
if total == 0:
    print('::error title=tests::no tests ran'); sys.exit(1)
sys.exit(1 if unexpected else 0)
PY
