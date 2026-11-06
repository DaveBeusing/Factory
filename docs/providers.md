# Provider routing and token budgets

Factory has a provider-independent routing boundary with two currently implemented adapters:

| Adapter | Execution | Read-only tasks | Token reporting | Account allowance |
| --- | --- | --- | --- | --- |
| Codex `exec` / `app-server` | Workspace write | Planning and reviews | Agent-reported | Account windows if supplied by Codex CLI |
| Ollama local API | **Not supported** | Text-only planning and reviews | Final `prompt_eval_count` and `eval_count`, if supplied | Unavailable |

The Ollama adapter uses the native .NET HTTP client, does not invoke repository commands, and accepts only an unauthenticated HTTP loopback endpoint. It receives the bounded context embedded in the prompt; it cannot browse the repository independently. A route that does not satisfy the role's capabilities fails closed, particularly for builder assignments.

## Setup

Install a functioning Codex CLI as described in the root [README](../README.md). Optionally install and start Ollama separately. Download a suitable model using Ollama's own documented procedures.

Modify the existing `~/.factory/settings.json` **without replacing controller credentials**, adding provider profiles like:

```json
{
  "providers": [
    { "id": "codex", "kind": "codex", "runtime": "exec", "model": "", "enabled": true, "tokenLimit": 0 },
    { "id": "ollama-local", "kind": "ollama", "model": "your-installed-model", "endpoint": "http://127.0.0.1:11434", "enabled": true, "tokenLimit": 250000 }
  ]
}
```

The values above are illustrative; do not assume a model exists locally. Model selection and compatibility depend on the actual external provider installation. Factory does not install models or authenticate on behalf of the operator. Existing settings continue to default to Codex.

In Mission Control select **Projects → Inspect → Configure AI providers**. Choose profiles for Planning, Builder, Review and optionally individual Code, Architecture, Security and UX reviews. A role without a project-specific assignment uses Codex by default, except individual reviews may inherit the project's general Review assignment. Running project jobs must complete before routes/budgets can be changed. The `/api/projects/{id}/providers` endpoint accepts route and budget dictionaries for the entire project; omitted values remove an override.

## Budget semantics

Provider profiles may specify a `tokenLimit`, a locally enforced lifetime cap for all projects using that profile ID. A project may additionally set a lifetime per-provider cap. Both zero and absence mean that particular local cap is not configured; they **do not** imply unlimited external provider account allowance.

For configured local caps, Factory makes a transactional invocation reservation before dispatch and records attributed usage when the adapter reports it. Concurrent active/held reservations count as liabilities. A final provider usage report releases unused capacity; interrupted, unreported or unknown usage remains held until an administrator explicitly reconciles it with evidence through **Capacity → Held provider reservations**. Such reconciliation does not automatically discharge any separate goal-ledger liability.

The goal budget remains a separate control: execution runs are still governed by the existing goal ledger, and goal-specific plan generation is accounted for in provider usage and goal-level consumed/reserved totals. Usage from before this migration cannot be reconstructed and is not assigned to an invented provider.

The Capacity page distinguishes input tokens, output tokens, reservations, total local limit/remaining, and provider account allowances. Codex weekly/short-window account percentages are displayed only if reported by the Codex CLI; other providers show unavailable. An account percentage is **never** derived from local usage and is not interchangeable with a local token ceiling.

Provider pricing, monetary billing, provider-specific remaining API credits and remote-worker heterogeneous routing are not implemented. There is no automatic provider fallback: a missing provider or unsupported required capability blocks that action rather than silently switching or relaxing a security policy.

## Security

Configuration and credentials belong under `.factory`, never in Git. All project routes must use enabled profiles and permitted roles. The controller authorizes writes; UI hiding alone does not enforce permissions. Third-party model/service terms and the safety of transmitted prompt context remain operator responsibilities. The local Ollama adapter uses only loopback transport and no credentials.
