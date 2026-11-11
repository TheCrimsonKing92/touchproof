# a346 Reviewer criterion evidence gap

Pipeline correctness repair: `518781bc`. The supplied incident evidence establishes an evidence-closure defect, not a defect in the landed token/projection implementation.

Goal `a3460eab5ce54840950860fef6cf1bbf` landed as `168bfaacb814b52de4ed6f843cc65e1e542c4882`. Its final acceptance result passed candidate `4cb30058d1c92d5c36b2c195f582949517f9c642`:

- `.orchestrator/acceptance-gate-attempts/a3460eab5ce54840950860fef6cf1bbf/a3460eab-0-20261111002231238-2df7da1e64a442779c79efa412978aea.result.json`
- `.orchestrator/acceptance-gate-attempts/a3460eab5ce54840950860fef6cf1bbf/a3460eab-0-20261111002231238-2df7da1e64a442779c79efa412978aea.infrastructure-tests-balanced-2.trx`
- `.orchestrator/logs/a3460eab-9cc34940-20261110204546.out.log`

Independent review recorded `WorkerSpecificationComparisonTests` output `Baseline=capture-only`; the advertised temporary capture directory was absent afterward. The Reviewer log marked structured `criterion_index=2` as `not-verifiable`, with the matched actual-main/candidate specification-package comparison pending. Request `105ec2dc6de64f978d3d1127214dfb4a` nevertheless completed from that log as `ROLE_REPORT_EVIDENCE owner=reviewer criterion=3 verdict=pass`.

`RoleReportEvidenceClosure` used overall round pass and candidate currency without consulting the requested criterion. The repair shares the per-criterion Reviewer parsing/closure seam, requires a unique matching `met` verdict under the current brief mapping, and retains candidate/conductor-only-range checks. Source review does not discharge acceptance-owned execution obligations.

The original matched package capture and independent difference review remain unfinished follow-up evidence. Blocking this bad closure does not retroactively qualify a346, establish that a comparison ran, credit an unexecuted benchmark, or require a matched experiment for every ordinary gate. Original receipts and landed feature coverage remain intact. Receipt facts above come from this repair's supplied objective; this worker did not retrieve live orchestrator state or execute the historical experiment.
