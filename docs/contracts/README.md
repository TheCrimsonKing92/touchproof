# Shared test result contract

`test-result-record.v1.schema.json` is a pinned copy of crosswalk's `schema/v1/test-result-record.schema.json`, which is the master schema.
The fixtures under `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Fixtures/TestResultRecords/` mirror crosswalk's `schema/v1/fixtures/`, which is the master fixture set.
A change to the schema or to any fixture lands in both repositories in one multi-repository goal.
