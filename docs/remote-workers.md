# Remote workers and trusted execution

**Use separate credentials, explicit project grants and current capability evidence before accepting a remote job.**

[← Documentation](README.md) · [Configuration](configuration.md) · [Provider routing](providers.md)

> [!WARNING]
> A worker token authenticates a **worker identity**, not the host OS, tools or repository contents. Git worktrees do not sandbox processes. Do not expose an HTTP controller outside loopback without correctly configured TLS, authentication and deployment review.

## Trust model

| Actor | Credential | Authorization boundary |
| --- | --- | --- |
| Controller administrator | Locally generated `apiToken` | Admin APIs, settings, worker authorization |
| Remote worker | Controller-derived token for one stable worker ID | Worker API only; project grants and capability proofs still required |
| Remote executor | Separate executor secret | Executor turn/receipt API only |
| Human project member | Revocable project token | Scoped Viewer/Planner/Operator/Budget Reconciler routes |

Workers cannot call administrative endpoints with `FACTORY_WORKER_TOKEN`. Managed local workers started by Factory receive their scoped token automatically; external workers require explicit provisioning.

## Provision the controller

On the trusted controller host, after an authorized TLS deployment:

```powershell
.\dist\Factory.exe worker-token --id worker-01
.\dist\Factory.exe dashboard --listen 'https://0.0.0.0:8443'
```

The second command requires a correctly configured HTTPS certificate; it does not create one. Deliver the generated worker token through an approved secret channel, **never** via Git or process arguments.

## Start a remote worker

On the remote host with the compatible Factory build, Git, agent and required checks installed:

```powershell
$env:FACTORY_WORKER_TOKEN = '<token-for-worker-01>'
.\dist\Factory.exe worker --server 'https://<controller-host>:8443/' --workspace 'C:\FactoryWork' --id worker-01
```

Worker IDs must begin with an ASCII letter/digit and then contain only letters, digits, dots, underscores or hyphens (maximum 100 characters). The worker sends `X-Factory-Worker-Id` with requests; the controller verifies the token matches that identity.

## Grant projects and attest capabilities

Registration, a heartbeat or self-reported platform are **not** a grant. The controller requires a project assignment and nonexpired administrator-reviewed evidence for all capabilities required by the job. Provision through admin-only `PUT /api/workers/{id}/permissions`.

```powershell
# $adminToken must already be loaded through a secure operator secret mechanism.
$controller = 'https://<controller-host>:8443'
$projectId = '<registered-project-guid>'
$body = @{
    projectIds = @($projectId)
    reason = 'Reviewed the worker host and project assignment'
    verifiedCapabilities = @(
        @{ capability = 'native-agent'; evidence = 'Reviewed native agent setup' }
        @{ capability = 'git-cli'; evidence = 'Verified the target Git installation' }
        @{ capability = 'runtime:exec'; evidence = 'Verified the exec adapter' }
        @{ capability = 'native-checks'; evidence = 'Verified native checks for this project' }
    )
} | ConvertTo-Json -Depth 6

Invoke-RestMethod -Method Put -Uri "$controller/api/workers/worker-01/permissions" `
    -Headers @{ Authorization = "Bearer $adminToken" } `
    -ContentType 'application/json' -Body $body
```

The example is for a Codex `exec` project with native checks. Use `runtime:app-server` when the project actually uses app-server; add `container-checks` if any configured check uses Docker. A project/job may require additional supported capabilities. Unknown, missing, revoked or stale proofs block claims. Proofs expire after **30 days**; provisioning replaces the full grant set, so always include every intended project.

An attestation records operator judgment; it is not cryptographic proof that the host remains secure. Treat credentials, filesystem, network and provider access as separate OS/deployment safeguards.

## Remote completion and integration

When a worker submits a PR-ready result, the controller independently verifies the expected GitHub repository, configured base, job branch and source SHA, plus successful **required GitHub checks**. Missing or failing required checks block completion. Worker-supplied review text and logs remain worker-reported even when metadata checks pass.

The controller **does not merge**. Use an authorized PR process and `Factory.exe verify '<job-guid>'` after integration, with the local base updated to the verified remote base when required.

For mixed remote provider routes, use a compatible **v2 worker** and matching controller/executor builds. Remote turn receipts and account allowance semantics are described in [Provider evidence and mixed routing](providers.md#provider-evidence-contract).

## Revocation and incidents

1. Drain or disable the worker's eligibility; do not assume an active process was killed.
2. Stop any running worker/executor through its legitimate host operator.
3. Preserve current leases, worktrees and dispatch/receipt records; do not blindly repeat uncertain Git or provider side effects.
4. Revoke the project grant/capability or rotate controller worker key as appropriate.
5. Reconcile held reservations and failed claims using [Recovery](recovery.md).

For project human roles and subject rotation, see [Operator authorization](operator-authorization.md). For deployment credentials, see [Configuration](configuration.md#credential-boundaries).
