"""Require real, passing test execution rather than a successful run of skipped tests."""
from pathlib import Path
import sys
from xml.etree import ElementTree


def main():
    reports = list(Path(sys.argv[1]).rglob("*.trx"))
    if not reports:
        raise SystemExit("No test result reports were produced.")
    total = 0
    for report in reports:
        root = ElementTree.parse(report).getroot()
        counters = root.find("{*}ResultSummary/{*}Counters")
        if counters is None:
            raise SystemExit(f"Missing test counters: {report}")
        expected = int(counters.get("total", "0"))
        passed = int(counters.get("passed", "0"))
        if expected == 0 or passed != expected:
            raise SystemExit(f"Every test must execute and pass: {report} ({passed}/{expected} passed)")
        total += passed
    print(f"Verified {total} passing tests across {len(reports)} reports; none skipped.")


if __name__ == "__main__":
    main()
