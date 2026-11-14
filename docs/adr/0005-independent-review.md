# ADR 0005 · Independent review provenance

Accepted for implementation. Review eligibility and identity are checked against policy. Persist the reviewed commit, reviewer identity and execution provenance. Missing evidence cannot satisfy a required independent review. Enforcement for execution reviews belongs to a later package.

The controller can accept an independent review after a local build finishes and releases its lease. Configure `FACTORY_REVIEW_PUBLIC_KEY` on the controller with the external review authority's PEM public key. Keep the corresponding private key outside the controller, builders and workers. The authority must authenticate the reviewer and establish read-only separation from the builder before signing a PASS. A builder or reviewer session ID supplied by an adapter alone does not establish independence.

Send the signed decision to the authenticated administrator endpoint `POST /api/review-approvals`. Sign the UTF-8 bytes of `Json.Stringify(ReviewApproval)` with RSA-PSS and SHA-256, then Base64-encode the signature. The decision identifies the project, job, execution generation, latest completed cycle, commit, policy and project contract revisions, reviewer and builder identities and sessions, and expiry. `GET /api/jobs/{id}/review-approved` reports current eligibility. Consult it for a manual PR handoff and pin the PR to the reviewed commit. Required GitHub CI still runs against the PR.

Remote completion closes the controller's reserved review cycle in the completion transaction. The controller records its own disposition and keeps bounded worker evidence with UNKNOWN assurance. A reported commit or verdict from a worker does not authorize a PR.
