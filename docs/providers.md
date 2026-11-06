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

Provider profiles may specify a `tokenLimit`, a locally enforced lifetime cap for all projects using that profile ID. A project may additionally set a lifetime per-provider cap. Both zero and absence mean that particular local cap is not configured; they **do not** imply unlimited external provider account allowance.

For configured local caps, Factory makes a transactional invocation reservation before dispatch and records attributed usage when the adapter reports it. Concurrent active/held reservations count as liabilities. A final provider usage report releases unused capacity; interrupted, unreported or unknown usage remains held until an administrator explicitly reconciles it with evidence through **Capacity → Held provider reservations**. Such reconciliation does not automatically discharge any separate goal-ledger liability. After a controller or local runner exits unexpectedly, run `factory recover` to move its active provider reservations to held. A new planning turn also checks for terminated owners. Recovery leaves the reserved balance in place until final usage is verified. Stop older Factory processes before upgrading a schema 7 database. The migration moves its active provider reservations to held because those rows have no recorded process owner.

The goal budget remains a separate control: execution runs are still governed by the existing goal ledger, and goal-specific plan generation is accounted for in provider usage and goal-level consumed/reserved totals. Usage from before this migration cannot be reconstructed and is not assigned to an invented provider.

The CLI command `factory providers` shows configured profile metadata, project role assignments, local budget limits and attributed consumption without exporting credentials. The original `factory usage` command continues to expose account-reported Codex allowance windows.

The Capacity page distinguishes input tokens, output tokens, reservations, local token ceilings and account allowances. The project-filtered Goal planner shows project usage and the tighter of the profile and project ceilings. The goal budget inspector also shows attributed input and output tokens by provider, model and role. These historical rows may cover less usage than the full goal ledger. Codex weekly/short-window account percentages are displayed only if reported by the Codex CLI; other providers show unavailable. An account percentage is **never** derived from local usage and is not interchangeable with a local token ceiling.

Capacity and the `factory providers` command show provider-reported USD totals by provider profile and project. They sum the estimates the CLI returned, including structured error results when a cost is present. A missing report stays unknown, and historical records keep a null cost after upgrading to schema 9. Claude Code reports an estimate that can differ from the invoice. Costs remain separate from token ceilings and account quotas. Provider-specific remaining API credits and remote-worker heterogeneous routing are unavailable. Remote claims are therefore refused for projects with explicit provider assignments or provider budgets, or whenever a provider-wide token cap is configured. This is a fail-closed transition until the remote claim protocol can carry and independently verify provider policy and usage provenance. There is no automatic provider fallback: a missing provider or unsupported required capability blocks that action rather than silently switching or relaxing a security policy.

## Security

Configuration and credentials belong under `.factory`, never in Git. All project routes must use enabled profiles and permitted roles. The controller authorizes writes; UI hiding alone does not enforce permissions. Third-party model/service terms and the safety of transmitted prompt context remain operator responsibilities. The local Ollama adapter uses only loopback transport and no credentials.
