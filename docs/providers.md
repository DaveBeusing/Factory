# Provider routing and token budgets

Factory has a provider-independent routing boundary with three currently implemented adapters:

| Adapter | Execution | Read-only tasks | Token reporting | Account allowance |
| --- | --- | --- | --- | --- |
| Codex `exec` / `app-server` | Workspace write | Analysis, planning and reviews | Agent-reported | Account windows if supplied by Codex CLI |
| Claude Code CLI | **Not supported** | Analysis, planning and reviews | CLI JSON, including cache input tokens when present | Unavailable |
| Ollama local API | **Not supported** | Text-only analysis, planning and reviews | Final `prompt_eval_count` and `eval_count`, if supplied | Unavailable |

The Ollama adapter uses the native .NET HTTP client, does not invoke repository commands, and accepts only an unauthenticated HTTP loopback endpoint. It connects directly with redirects and proxies disabled. It receives the bounded context embedded in the prompt; it cannot browse the repository independently. A route that does not satisfy the role's capabilities fails closed, particularly for builder assignments.

## Claude Code

Install and authenticate a native Claude Code CLI that supports `--safe-mode` and `--restricted`. Factory discovers the executable under the user profile's `.local/bin` or on an absolute `PATH` entry. Set `FACTORY_CLAUDE_EXECUTABLE` to an absolute executable path to override discovery. Use the provider's own authentication and configure a profile with `"kind": "claude"` and an optional `"model"` such as `"sonnet"`.

Factory starts independent print-mode turns, disables session persistence, and restricts the CLI to Plan mode and the Read, Glob and Grep tools with no MCP servers. Safe mode disables Claude Code customizations. Restricted mode confines the CLI file tools to the working directories. These are CLI controls rather than OS sandboxing. Prompts and files read by the CLI can be sent to its configured inference service. See the [CLI reference](https://code.claude.com/docs/en/cli-reference) and [programmatic mode](https://code.claude.com/docs/en/headless).

## Setup

Install a functioning Codex CLI as described in the root [README](../README.md). Optionally install and start Ollama separately. Download a suitable model using Ollama's own documented procedures.

Modify the existing `~/.factory/settings.json` **without replacing controller credentials**, adding provider profiles like:

```json
{
  "providers": [
    { "id": "codex", "kind": "codex", "runtime": "exec", "model": "", "enabled": true, "tokenLimit": 0 },
    { "id": "claude-review", "kind": "claude", "model": "sonnet", "enabled": true },
    { "id": "ollama-local", "kind": "ollama", "model": "your-installed-model", "endpoint": "http://127.0.0.1:11434", "enabled": true, "tokenLimit": 250000 }
  ]
}
```

The values above are illustrative; do not assume a model exists locally. Model selection and compatibility depend on the actual external provider installation. Factory does not install models or authenticate on behalf of the operator. Existing settings continue to default to Codex.

In Mission Control select **Projects → Inspect → Configure AI providers**. Choose profiles for Planning, Builder, Review and optionally individual Code, Architecture, Security and UX reviews. An unassigned analysis role uses deterministic inspection only; other roles without a project-specific assignment use Codex by default, except individual reviews may inherit the project's general Review assignment. Running project jobs must complete before routes/budgets can be changed. The `/api/projects/{id}/providers` endpoint accepts route and budget dictionaries for the entire project; omitted values remove an override.

## Budget semantics

Factory tracks four separate quantities.

| Quantity | Scope and source |
| --- | --- |
| Goal budget | Operator-defined token total shared by planning and execution for one goal |
| Provider caps | Optional Factory-local lifetime token limits for a provider profile and for a profile within a project |
| Provider account windows | Weekly or shorter allowance data reported by the authenticated Codex CLI |
| Provider-reported cost | USD estimates supplied by the adapter for individual turns |

### Goal budget and job reservations

Available goal tokens equal the configured total minus observed usage and the unused portion of active or held reservations, with a minimum of zero. Goal-specific planning also counts against the goal's budget. Provider-attributed job usage describes the same usage recorded in the job ledger.

On a local or remote claim, Factory reserves up to `goalReservationTokens` in the same SQLite transaction as the claim. The default is 8,192, configurable from 256 to 65,536. Independent jobs with disjoint scopes can reserve separate shares of the same goal. Builder and reviewer turns share the job's allowance and need at least 256 tokens before starting. Factory debits usage under the current execution lease and generation. Extensions are bounded by goal availability and the configured per-job ceiling. Remote extensions require controller authorization.

Factory releases unused job capacity automatically when execution reaches `completed_local` or `pr_ready` with confirmed final usage within the reservation. An interruption, missing final usage or overrun leaves the job reservation held. A delayed provider report can exceed a reservation even when Factory requests cancellation on exhaustion. The ledger records the observed amount. See [Settle held usage](recovery.md#settle-held-usage) for the operator workflow.

Open **Goal planner → Goal & budget** to see consumption, outstanding reservations, available capacity and attributed usage by provider, model and role. `GET /api/goals/{id}/budget` exposes the ledger summary without lease credentials. Older job usage can appear in goal totals without provider attribution.

### Provider caps and turn reservations

Set `tokenLimit` on a provider profile to cap its lifetime token consumption across projects. Set a project cap for the same profile in **Projects → Inspect → Configure AI providers**. A positive cap subtracts observed input and output usage plus unused active and held reservations from its limit. The turn must fit within the remaining allowance under every configured cap. Zero or an omitted cap leaves that local limit unset. Provider account quotas are reported separately.

Factory reserves tokens transactionally before capped turns and goal-specific planning. For a goal-funded job with an uncapped provider, a zero-size provider reservation still tracks the turn for recovery. Final usage releases the unused provider reservation. Interrupted or unreported turns remain held for review. See [Recovery](recovery.md) for same-host and cross-host procedures and settlement order.

Remote claims are refused for projects with explicit provider assignments or project provider caps, and when a provider-wide cap is configured. A missing provider or unsupported role capability blocks the requested action. Factory requires an explicit route change to use another provider.

### Account windows and reported costs

**Capacity** and `factory providers` show attributed consumption, local limits and reported USD totals by profile and project. The project-filtered **Goal planner** shows project consumption and capacity under the applicable profile and project limits. Totals sum the cost estimates actually returned, including structured error results with a cost. Missing values stay unknown, including historical rows upgraded to schema 9. Claude Code estimates can differ from an invoice. Authenticated live verification of those estimates is tracked in [issue #22](https://github.com/DaveBeusing/Factory/issues/22).

`factory usage` and administrator-authenticated `GET /api/usage` expose Codex account windows. Factory queries the authenticated CLI's local app-server endpoint and caches successful observations. When reported, Capacity displays used and remaining percentages, reset times and observation age. Missing account percentages and unsupported provider credit balances are unavailable. Factory does not infer them from local usage.

## Security

Configuration and credentials belong under `.factory`, never in Git. All project routes must use enabled profiles and permitted roles. The controller authorizes writes; UI hiding alone does not enforce permissions. Third-party model/service terms and the safety of transmitted prompt context remain operator responsibilities. The local Ollama adapter uses only loopback transport and no credentials.
