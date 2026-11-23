# Limescape Umbraco Agent API

Generic NuGet package for Umbraco 17 LTS / .NET 10, available under the LOPL 1.0 (see [Licence](#licence)). It exposes operations on repository materials (content items with files and metadata, such as learning materials) twice,
over one service layer:

- an **MCP server** (Streamable HTTP) for AI agents, such as Limescape autonomous agents;
- **REST endpoints** for screens, such as the guided screens of the Limescape Navigator app.

Every call runs **as the signed-in person**: the caller sends that person's own OAuth2/OIDC access token of the
tenant identity provider. The package validates it, maps its claims to roles, checks rights server-side and makes the
person the acting user of the [Compliance Pack](https://github.com/TrueLimeNL/limescape.umbraco.compliance), so audit entries and
content rules apply to the real person, whichever route was used.

> Status: version 1.0.0. Included: authentication, the service layer with `list_collections`,
> `propose_metadata_context`, `create_material`, `add_elements`, `add_element_from_session`, `remove_element`, `update_material`, `get_material`, `get_detail_link`,
> groups and typed links (`list_groups`, `add_to_group`, `remove_from_group`, `add_relation`, `remove_relation`),
> `submit_material`, `archive_material`, `set_owners`, review (`list_review_queue`, `approve_material`, `reject_material`),
> retention management (`list_retention`, `set_retention_class`, `set_legal_hold`, `clear_legal_hold`), field state (`set_metadata_field_state`), and REST for the same
> operations plus `GET /{key}`. Search is not part of this package: it feeds the search index of the platform (see
> [Element content for the index sync](#element-content-for-the-index-sync)). Moving a material to the recycle bin and
> restoring it are standard Umbraco functions, guarded by the Compliance Pack.

## Install

Reference the package (it depends on `Limescape.Umbraco.Compliance`) and keep the standard `AddComposers()` and
`UseUmbraco().WithMiddleware(...).WithEndpoints(...)` host pipeline. The auto-discovered `McpServerComposer` registers
services and a pipeline filter; no manual mapping is needed. The feature is disabled by default.

## Configuration

Section `Limescape:AgentApi` (environment variables use `Limescape__AgentApi__...`). Changes need a host restart.

| Key | Default | Purpose |
|---|---|---|
| `Enabled` | `false` | Maps the MCP and REST endpoints |
| `EndpointPath` | `/mcp` | MCP endpoint; literal absolute path, segments of ASCII letters, digits, `-` and `_` |
| `RestBasePath` | `/api/learning-materials` | Base path of the REST endpoints; same rules |
| `Authentication:Authority` | empty | OIDC authority of the tenant identity provider, for example `https://login.microsoftonline.com/<tenant>/v2.0`. Discovery provides the signing keys. **Empty: every request gets 401** |
| `Authentication:Audiences:n` | none | Accepted `aud` values (the content layer's app id URI or client id). None: every request gets 401 |
| `Authentication:ValidIssuers:n` | discovered issuer | Only when the token issuer differs from the discovery document |
| `Authentication:SubjectClaim` | `sub` | Claim with the stable person id (owner, audit). Entra ID tenants usually use `oid` |
| `Authentication:NameClaim` | `name` | Display name |
| `Authentication:RoleClaims:n` | `roles`, `groups` | Claims read for the role mapping |
| `Authentication:RequiredScope` | empty | Delegated scope a token must carry to change anything, for example `materials.readwrite`. Empty: any delegated scope |
| `Authentication:RoleMapping:<role>:n` | none | Claim values (app role names or group ids) that grant `reader`, `developer`, `reviewer` or `functionalManager` |
| `UserToken:TrustedIssuers:n:Issuer`, `:PublicKeys:n`, `:Audience` | none | Trusted issuers of **delegated person tokens** (see below): exact `iss`, PEM public keys (EC, or RSA of 2048 bits or more; several for rotation) and optionally the required `aud` (default `UserToken:Audience`). Empty: the Compliance Pack's `Compliance:ActingUser:TrustedIssuers` are used with `UserToken:Audience`. Both empty: only identity-provider tokens are accepted |
| `UserToken:Audience` | `agent-api` | Required `aud` of delegated person tokens for issuers without their own `Audience` |
| `Content:CollectionDocumentTypes:n` | none | Document types whose items are collections (where materials are created) |
| `Content:MaterialCompositionAlias` | `lsLearningMaterialBase` | A document type with this composition is a material type |
| `Content:ElementMediaTypeAlias` | `lsElement` | Media type of a file of a material |
| `Content:ElementsRootKey` | media root | Media folder under which one folder per material is created |
| `Content:*Property` | object model aliases | Property aliases: `title`, `description`, `status`, `sharingLevel`, `license`, `source`, `owners`, `elements`, `kind`, `linkUrl`, `archivedAt` on materials; `category`, `sha256` on elements |
| `Content:Fields:<alias>:Labels:<nl\|en>` | none | Label of a field per language for the metadata context. Without entry the label is the alias in both languages |
| `Content:Fields:<alias>:Type` | derived from the editor | Field type for a form: `string`, `text`, `list`, `number`, `date`, `url`, `boolean` or `other` (a field the content layer sets itself is always `system`) |
| `Content:Fields:<alias>:Values:<code>:<nl\|en>` | none | Label of an allowed code of a list field per language. Without entry the label is the code |
| `Content:MaterialTypes:<alias>:Labels:<nl\|en>` | none | Label of a material type per language. Without entry the label is the alias |
| `Content:GroupDocumentTypeAlias` | `lsMaterialGroup` | Document type of a group of materials; empty: no groups |
| `Content:GroupMembersProperty` | `members` | Ordered multi-node tree picker of the group's materials |
| `DetailLinkTemplate` | empty | Detail page URL under the Navigator domain with `{key}`, for example `https://navigator.example.org/materiaal/{key}`. https only (http for loopback) |
| `MaxInlineElementBytes` | 20 MB | Largest file accepted inline (base64) by `add_elements` |
| `UploadSessions:SigningSecret` | empty | Shared secret (at least 32 characters) with which the platform signs verified upload sessions. **Empty: `add_element_from_session` gives `not_configured`** |
| `UploadSessions:PathPrefix` | `materials/` | Folder of the media file system in which session files may be registered; ends in `/` |
| `UploadSessions:MaxBytes` | 10 GB | Largest file a session may register |
| `UploadSessions:CategoryMaxBytes:<category>` | none | Largest file per element category code, for example `basic`; only takes effect below `MaxBytes` |
| `UploadSessions:MaxSignatureLifetimeSeconds` | `3600` | Longest accepted time between now and the signed `expiresAt` |
| `UploadSessions:VerifySha256` | `true` | Read the stored file once and compare its SHA-256 with the signed one. Off: trust the platform's verification, check only the size |

A material type is a document type composed with `MaterialCompositionAlias`, allowed as child of the collection's
document type. The `owners` property (label, JSON array of person ids) and the `elements` property (media picker) are
written by the service only.

## Authentication and roles

- The token must be a JWT access token signed by the authority, for one of the audiences, not expired. Otherwise the
  endpoint answers 401 with `WWW-Authenticate: Bearer`. The scheme `Limescape.AgentApi` is never the default scheme,
  so Umbraco's backoffice and member sign-in are unaffected.
- **Only delegated user tokens may change state**: the token must carry the delegated scope (`scp`, or
  `scope` for generic OIDC providers) and must not be an application token (`idtyp=app` in Entra ID). A client-credentials
  token of a team or service can read but every write returns `delegated_token_required`.
- Roles come from `RoleMapping`; several roles add up. Without any role the person gets `forbidden` on every tool.
- If a trusted integration also sends a Compliance Pack acting-user header, it must name the same person as the token
  (`acting_user_mismatch` otherwise).

### Delegated person tokens of a trusted issuer

A person who has no identity-provider token for the content layer (a local development slice, a tenant without an
on-behalf-of flow) can be represented by a **delegated person token** that a trusted issuer, typically the Limescape
platform, signs with its own key: the same key model as the Compliance Pack's acting-user header, and the same keys
can be used (`UserToken:TrustedIssuers` falls back to `Compliance:ActingUser:TrustedIssuers`). The route is off until
an issuer is configured; with one configured, a bearer token whose `iss` names it is validated by the trusted-issuer
scheme and every other bearer token still goes to the identity provider. The production route for tenants with an
identity provider remains on-behalf-of with that provider.

The token is a JWT in compact JWS form. Header: `alg` asymmetric only (`ES256` by default; `ES*`, `RS*`, `PS*`
accepted, `none` and `HS*` refused), `kid` optional. Payload:

| Claim | Required | Meaning |
|---|---|---|
| `iss` | yes | Name of the trusted issuer, exactly as configured |
| `aud` | yes | The issuer's `Audience`, or `UserToken:Audience` (`agent-api`) |
| `sub` | yes | Stable person id in the issuing system (max 255); the owner id and audit actor, whatever `Authentication:SubjectClaim` says for identity-provider tokens |
| `name` | no | Display name (max 255) |
| `email` | no | Lowercase e-mail address (max 255); accepted, not used by this package |
| `roles` | no | Role values as the identity provider would send them, mapped through `Authentication:RoleMapping`; without any mapped role the person gets `forbidden` on every operation |
| `channel` | no | Channel the person acts through at the issuer, for example `navigator` or `assistant`; lowercase `[a-z][a-z0-9_-]{0,31}`. The audit channel stays `app` (REST) or `assistant` (MCP), as for identity-provider tokens |
| `iat`, `exp` | yes | Issued at and expiry (seconds since epoch); `exp - iat` at most **600 seconds**, valid now within a clock skew of 60 seconds |

Example payload:

```json
{ "iss": "lmb-local-connector", "aud": "agent-api", "sub": "4f1c…", "email": "a.person@example.org", "name": "A. Person", "roles": ["Materials.Developer"], "channel": "navigator", "iat": 1791460800, "exp": 1791461400 }
```

A delegated person token is a person token by contract: it needs no delegated scope and is never an application
token, so it may change state. Rights, ownership and visibility are identical to an identity-provider token with the
same person id and roles. **Ownership differs per issuer**: the owner id of a delegated token is its `sub` (for the
Limescape platform the lowercase e-mail address), while an identity-provider token gives `Authentication:SubjectClaim`
(`oid` for Entra ID). A material created through the platform is therefore owned by the e-mail id, and the same person
with an Entra token is another owner unless the tenant aligns the two ids. Which id a tenant uses, and whether the
platform forwards the identity provider's token (on-behalf-of) instead, is chosen per tenant when that flow is set up. Refused tokens (symmetric or missing signature, another key, unknown issuer, wrong or missing
`aud`, expired, issued in the future, longer than 600 seconds, no `iat`, `exp` or `sub`) get 401 with
`WWW-Authenticate: Bearer`. The audit records the issuer (`lmb-local-connector` above) next to the person, so an
entry made through the platform is distinguishable from one made with the identity provider's token.

## Metadata context

`GET /metadata-context` (MCP `propose_metadata_context`) describes the object model so a form, a validation and a prompt can be built from it without a copy of the model. One answer per material type; nothing of the earlier answer is removed.

```json
{
  "materialTypes": [
    {
      "alias": "lsDocument", "name": "#lsObjectModel_lsDocument", "description": null,
      "labels": { "nl": "Document", "en": "Document" },
      "fields": [
        {
          "alias": "license", "name": "#lsObjectModel_license", "editor": "Umbraco.DropDown.Flexible",
          "mandatory": false, "allowedValues": ["CC0-1.0", "CC-BY-4.0"], "target": "license",
          "type": "list", "required": true,
          "labels": { "nl": "Licentie", "en": "License" },
          "values": [
            { "value": "CC0-1.0", "labels": { "nl": "CC0 1.0 (publiek domein)", "en": "CC0 1.0 (public domain)" } },
            { "value": "CC-BY-4.0", "labels": { "nl": "CC BY 4.0 (naamsvermelding)", "en": "CC BY 4.0 (attribution)" } }
          ]
        }
      ]
    }
  ],
  "collections": [], "element": { "categories": [], "maxInlineBytes": 20971520 }
}
```

| Field | Meaning |
|---|---|
| `type` | `string`, `text`, `list`, `number`, `date`, `url`, `boolean`, `system` (set by the content layer, never by the caller) or `other`. A configured `Content:Fields:<alias>:Type` wins, otherwise it follows the property editor (a dropdown is `list`, the link address is `url`) |
| `required` | The field must be filled to **submit** the material for review, not for a draft (the title is needed to create one). True when the property is mandatory in the object model or when the Compliance Pack's submission rule requires it for this type (`Compliance:Submission:RequiredFields`, a schema snapshot, or any other `IRequiredFieldProvider` that answers per type). It is the same rule `submit_material` and the save guard apply, so a form and the server agree. `mandatory` stays Umbraco's own flag and keeps its meaning |
| `labels` | Label per language, always `nl` and `en`, plus any other language configured. A language without a configured label gets the alias, so a consumer never has to supply a fallback. The same on the material type |
| `values` | For a list field every code, each with `labels` per language (the code where none is configured) and `deprecated` (see "Archived fields and deprecated values"); `null` for other fields |
| `allowedValues` | For a list field the codes that can newly be chosen: `values` without the deprecated codes |
| `archived` | The field is archived: its values stay on the materials, no new value can be written. A form or a proposal leaves the field out; `required` is `false` for it |

### Archived fields and deprecated values

A field can be taken out of use without losing what materials hold. A field is **archived** when its property
sits in the property group `Gearchiveerd` of its document type (alias or name, case-insensitive; configure another name
in `Compliance:FieldState:ArchivedGroup`), or when a functional manager flagged it with `set_metadata_field_state`. A
choice code is **deprecated** when that call lists it. For both the same rule holds on every route that writes (create and
update over REST and MCP, and any other save through the Compliance Pack's save handler: the Management API, backoffice
and code):

- a new or changed value in an archived field is refused with `field_archived` (400);
- a deprecated code is refused with `value_deprecated` (400) unless the material held that code already;
- a value the material holds stays valid: an update of other fields never touches it, and sending it again unchanged is
  accepted. Clearing a value is not a new value and stays possible.

`PUT /metadata-fields/{alias}/state` (MCP `set_metadata_field_state`) sets the state of one field:
`{ "version", "archived": true|false, "deprecatedValues": ["code", ...] }`. The list of deprecated codes replaces the
earlier list. The answer is `{ alias, archived, archivedByGroup, deprecatedValues, version }`; send that `version` with
the next change to refuse it when somebody changed the state in between (`version_conflict`, 409; leave it out the first
time). Only a `functionalManager` with the person's own token may call it (`forbidden`, `delegated_token_required`, both
403). An unknown field, a system field (and the title, kind, link and sharing fields every material needs) or a code the
field does not have is `invalid` (400). A field in the archived group stays archived whatever the flag says
(`archivedByGroup: true`); restoring it means moving the property out of the group. The state is stored per tenant in
Umbraco's key-value table (no migration) and each change is audited (`fieldStateChange`, entity `metadataField`, old and
new state, the person, channel and issuer). A repeated call with the same state changes nothing and writes no entry.

Labels and types are tenant configuration, never code. The image carries none; a tenant sets `Limescape:AgentApi:Content:Fields` and `:MaterialTypes` (see Configuration) in environment variables or in a profile file (`Limescape:ProfileConfigFile` in the content layer host).

`?materialType=lsVideo` (REST) or `materialType` (MCP) answers for that type alone; an unknown alias is `404` / `not_found`.

## Rights and visibility

| Operation | Who |
|---|---|
| `list_collections`, `propose_metadata_context` | any role (team tokens included) |
| `create_material` | `developer`, delegated token; the person becomes owner; status `draft`, never published |
| `add_elements`, `add_element_from_session`, `remove_element`, `update_material`, `add_to_group`, `remove_from_group`, `add_relation`, `remove_relation` | delegated token; owner with `developer`, or `functionalManager`; only unpublished `draft` or `rejected` materials that are not archived. For a link the other material only has to be visible to the caller (`not_found` otherwise) |
| `submit_material` | delegated token; owner with `developer`, or `functionalManager`; only unpublished `draft` or `rejected` materials, at the confirmed `version`, with the required fields filled |
| `archive_material` | delegated token; owner with `developer`, or `functionalManager`; only a published material that is not archived yet, at the confirmed `version` |
| `set_owners` | delegated token; owner with `developer`, or `functionalManager`; at the confirmed `version`; a published material only for a `functionalManager` (`forbidden` for its owner), an archived one never (`material_not_editable`). A caller who cannot see the material gets `not_found`, one who sees it without the rights `forbidden` |
| `list_groups` | any role |
| `list_review_queue` | `reviewer`: submitted and in-review materials the person does not own. Every other role gets an empty list (no role at all: `forbidden`) |
| `approve_material`, `reject_material` | delegated token; `reviewer` (else `forbidden`, whatever the material); the material visible to the person (else `not_found`); not an owner (`own_material`); status `submitted` or `inReview` (`material_not_reviewable`); at the confirmed `version`; a rejection with feedback (`invalid`) |
| `list_retention` | `functionalManager` (else `forbidden`) |
| `set_retention_class`, `set_legal_hold`, `clear_legal_hold` | delegated token; `functionalManager` (else `forbidden`, whatever the material); at the confirmed `version`; an unknown key `not_found` |
| `set_metadata_field_state` | delegated token (`delegated_token_required`); `functionalManager` (else `forbidden`); an unknown field, a system field or an unknown code `invalid`; optionally at the `version` of the earlier answer (`version_conflict`) |
| `get_material`, `get_detail_link`, `GET /{key}` | `functionalManager` and owners see drafts; `reviewer` sees drafts only in submitted/inReview. Every mapped role may read public publications; groups publications require matching validated group membership. Publication-only readers receive published values and no `version` |

Not visible and not found give the same answer (`not_found`), so no metadata leaks. No tool deletes, and the only tool
that publishes is `approve_material`: it writes the reviewer's approval record first and then publishes through the
Compliance Pack's publication gate, which checks that record for exactly this draft (see "Review and retention management").

## MCP tools

| Tool | Changes state | Arguments |
|---|---|---|
| `list_collections` | no | none |
| `propose_metadata_context` | no | optional `materialType` (one type alias; unknown gives `not_found`). Returns material types with fields (`alias`, `name`, `editor`, `mandatory`, `allowedValues`, `target`, and `type`, `required`, `labels`, `values`), collections and element categories. See "Metadata context" |
| `create_material` | yes | `collectionKey`, `materialType`, `title`, optional `description`, `license`, `sharingLevel`, `source`, `kind` (`file` default or `link`), `linkUrl` (required for `link`, an http(s) address; a link material takes no elements), `metadata` (other fields by alias) |
| `add_elements` | yes | `materialKey`, `elements`: `[{ fileName, category, contentBase64 }]` |
| `add_element_from_session` | yes | `materialKey`, `session`: the signed upload session from the platform, unchanged (see "Large files") |
| `remove_element` | yes | `materialKey`, `elementKey` (the element's `key` in `get_material`), `version` (as last read). Destructive: the file leaves the material and goes to the recycle bin of the media; see "Removing an element" |
| `update_material` | yes | `materialKey`, `version` (from the material as last read), optional `title`, `description`, `license`, `sharingLevel`, `source`, `kind`, `linkUrl`, `metadata`. A left-out field stays as it is; an empty string clears it (not the title) |
| `get_material` | no | `materialKey` |
| `get_detail_link` | no | `materialKey` |
| `list_groups` | no | none. Groups with `key` and `title` |
| `add_to_group` | yes | `materialKey`, `groupKey`. Appends the material; a member stays where it is |
| `remove_from_group` | yes | `materialKey`, `groupKey` |
| `add_relation` | yes | `materialKey`, `type` (`lsRelated`, `lsPartOf`, `lsVersionOf`), `targetKey`, optional `inverse` (default `false`). An existing link is kept |
| `remove_relation` | yes | `materialKey`, `type`, `targetKey`, optional `inverse` |
| `submit_material` | yes | `materialKey`, `version`. Status `submitted` |
| `archive_material` | yes | `materialKey`, `version`, optional `reason` (max 500 characters). Destructive: the material leaves publication for good |
| `set_owners` | yes | `materialKey`, `version`, `mode` (`replace`: the listed people become the owners; `add`: they are added, nobody is removed; no default), `owners`: person keys, at least one, each without spaces and at most 256 characters. Destructive: `replace` can take access away |
| `list_review_queue` | no | none. `ReviewQueueItem[]`, oldest first |
| `approve_material` | yes | `materialKey`, `version`, optional `comment` (max 2,000 characters). Records the approval and publishes |
| `reject_material` | yes | `materialKey`, `version`, `feedback` (required, max 10,000 characters). Status `rejected` |
| `list_retention` | no | optional `withinDays` (1 to 3,650, default 30), `state` (`running`, `expiring`, `ended`), `skip`, `take` (1 to 500, default 100) |
| `set_retention_class` | yes | `materialKey`, `version`, `classKey` (from `list_retention`). Destructive: replaces the material's class |
| `set_legal_hold` | yes | `materialKey`, `version`, optional `reason` (max 500 characters) |
| `clear_legal_hold` | yes | `materialKey`, `version`, optional `reason`. Destructive: lifts a deletion block |
| `set_metadata_field_state` | yes | `alias`, `archived` (true or false), `deprecatedValues` (the complete list of deprecated codes after the change), optional `version`. Archives or restores a field and deprecates codes; no value is removed. See "Archived fields and deprecated values" |

The descriptions of the writing tools say **REQUIRES CONFIRMATION**; configure human-in-the-loop on them in the
agent. Results are JSON text. Errors are tool errors (`isError: true`) whose text starts with a code:
`unauthenticated`, `forbidden`, `delegated_token_required`, `acting_user_mismatch`, `not_found`, `invalid`,
`material_not_editable`, `material_not_archivable`, `material_not_reviewable`, `own_material`, `version_conflict`,
`operation_cancelled` (an Umbraco or Compliance Pack rule refused the save), `not_configured`, `upload_session_invalid`,
`upload_session_used`. `update_material`, `remove_element`, `set_owners`, `remove_from_group`, `remove_relation`, `archive_material`,
`set_retention_class` and `clear_legal_hold` are marked destructive (they overwrite, remove or lift a protection; the old
values stay in the audit log). The group and link tools, the legal hold tools and `add_element_from_session` are idempotent.

## Removing an element

`remove_element` (MCP) and `DELETE {RestBasePath}/{key}/elements/{elementKey}?version=` (REST) remove one
file from a draft or rejected material. `elementKey` is the `key` of the element in `MaterialDetail.elements`; `version`
is required (`400 invalid` without) and checked under the content write lock, as in `update_material`.

- **Rights and state** as `update_material`, checked in this order: delegated token (`delegated_token_required`), material
  visible (`not_found`: an unknown material, or someone else's draft for a developer, reader or reviewer), owner with the
  `developer` role or `functionalManager` (`forbidden` for a caller who sees the material without the right, for example a
  reviewer of a submitted material or anyone on a public publication), draft or rejected, not published and not archived
  (`material_not_editable`), then the version (`version_conflict`, the current version is in the message).
- **Element lookup.** An unknown key, an element of another material, one that is already in the recycle bin and a
  material without elements are all `not_found`, so the answer never names a file of another material.
- **What happens.** The element leaves the working version of the material (one save, so the Compliance Pack audits an
  `update` of `elements`) and its media item moves to the recycle bin of the media (audit `trash`, with person, channel
  and issuer of the token; the webhook `MediaMovedToRecycleBin` fires). The file is **not** purged: removing it for good
  stays with the Compliance Pack's purge route and its deletion block, which also applies here; a refused
  move gives `409 operation_cancelled` and nothing changes. A published version keeps its own list of elements until the
  next approval, which is why a published material is not editable here.
- **Confidential elements** can be removed by the people who may remove any element (owner, functional manager). The
  response is the material as that caller sees it; it names no file of someone who may not see a confidential element.
- Not idempotent: a retry after success is `not_found` (the element is gone) or `version_conflict`.

## Updates and the version token

Every material carries `version`: its update date as an ISO-8601 UTC string with seven decimals, for example
`2026-10-08T16:47:39.3754680Z`. Treat it as opaque and send it back unchanged with `update_material` or `PUT /{key}`.
The service takes Umbraco's content write lock first, then reads the material, checks visibility, rights, editable state
and the version, and saves, all in one scope; a concurrent update waits and then gets `version_conflict` (409). The
version id is not used: Umbraco keeps it while a draft is saved again.

- Status, owners, elements and the archive date are set by the content layer only: in `metadata` they give `invalid`,
  as unknown members of the REST body as well. MCP binds named arguments only, so an extra argument changes nothing.
  Collection and material type cannot change either.
- `kind` and `linkUrl` follow the same rules as on create, checked on the values after the update: a link needs an
  http(s) address; a file has none (send `linkUrl: ""` when changing a link to a file); a material with elements cannot
  become a link.
- An update that changes nothing does not save, writes no audit entry and keeps the version.
- `metadata.sharingGroups` (create and update) must be a JSON array of non-empty group ids, for example
  `"[\"group-a\"]"`; it is stored de-duplicated, anything else gives `invalid`. An empty string clears it, and a
  `groups` publication without groups is then readable by owners, reviewers and functional managers only.
- Only callers who see the draft get a `version`; a reader of the publication gets `null` and cannot update.
- A published material is not editable, whatever its stored status (the publication gate can leave `status` at
  `draft`). Editing a new version of a published material is not built yet.
- The read for the check can come from Umbraco's repository cache. On a single instance that cache is updated inside
  the writing scope; with several instances behind a load balancer the check is only as fresh as the cache refresh
  between instances.

## Large files: verified upload sessions

`add_elements` takes files inline up to `MaxInlineElementBytes`. Larger files never pass through the
content layer: the platform's upload screen writes them as a block blob into the media storage (resumable), its worker
commits the blob and checks length and SHA-256, and the platform then calls `POST /{key}/verified-uploads` (or the MCP
tool `add_element_from_session`) with the person's own token and the signed evidence. The content layer registers the
existing file as an element through `IMediaService`; it is not copied or sent again.

Body (JSON, camelCase):

| Field | Meaning |
|---|---|
| `sessionId` | Upload session id in the platform |
| `blob` | Path of the committed file relative to the media file system, under `PathPrefix`, for example `materials/7/<sessionId>/lecture.mp4`. With the Azure media provider this is the blob name in the media container (below its root path) |
| `length` | Verified size in bytes |
| `sha256` | Verified SHA-256, 64 lowercase hex characters |
| `etag` | ETag of the blob at verification (signed; the media file system exposes no ETag, so the host checks size and checksum instead) |
| `category` | Element category code |
| `fileName` | File name of the element |
| `confirmationId` | Id of the person's recorded save confirmation in the platform |
| `expiresAt` | End of validity of the signature, Unix seconds |
| `signature` | HMAC-SHA256 with `UploadSessions:SigningSecret`, lowercase hex |

The signed text is eleven lines joined by `\n` (no final newline): `limescape-upload-session-v1`, the material key
(lowercase, with hyphens), then `sessionId`, `blob`, `length` (decimal), `sha256`, `etag`, `category`, `fileName`,
`confirmationId`, `expiresAt` (decimal). In Python:

```python
text = "\n".join(["limescape-upload-session-v1", str(material_id), session_id, blob, str(length), sha256,
                  etag, category, file_name, confirmation_id, str(expires_at)])
signature = hmac.new(secret.encode(), text.encode(), hashlib.sha256).hexdigest()
```

Test vector (`UploadSessionSignatureTests`): secret `test-secret-of-at-least-32-characters!!`, material
`3f2504e0-4f89-41d3-9a0c-0305e82c3301`, session `6b1f0c2e-7d4a-4c55-9a51-0f4f6c3e2a10`, blob
`materials/7/6b1f0c2e-7d4a-4c55-9a51-0f4f6c3e2a10/lecture.mp4`, length `104857600`, sha256
`9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08`, etag `"0x8DCE1F2A3B4C5D6"` (with the quotes),
category `multimedia`, file name `lecture.mp4`, confirmation `c0ffee00-0000-4000-8000-000000000001`, expiresAt
`1791547200` gives `ca25c02f388b0e554756c37f9d1438ed0a4fbe32823ddd1cd0533544a42adf7c`.

Checks, in this order; nothing is written until all pass:

1. Delegated token (403 `delegated_token_required`); secret configured (501 `not_configured`).
2. Signature over this material key (403 `upload_session_invalid`): another secret, any changed field, another material
   or no signature. `expiresAt` in the past or further away than `MaxSignatureLifetimeSeconds` gives the same code.
3. The rights and state of `add_elements`: not visible 404, not owner with developer role or manager 403, published or
   not draft/rejected or archived 409 `material_not_editable`; a link material 400.
4. File name and category as for inline files; `sha256` format; `length` within `CategoryMaxBytes` for the category and
   `MaxBytes` (400 `invalid`).
5. The file exists at `blob` under `PathPrefix` without `.` or `..` segments, its stored size equals `length` and, with
   `VerifySha256`, its SHA-256 equals `sha256` (400 `invalid`). The file is read before any lock is taken.
6. Under the media and content write locks: rights and state again, then the session file is looked up by its media
   URL. Already an element of this material: 200 with the material, nothing written (a retry after an ambiguous
   response). Already registered elsewhere: 409 `upload_session_used`. Otherwise one element media item is created in
   the material's element folder with `umbracoFile` set to the file's URL, extension, size (`umbracoBytes`, int64 as
   text), `sha256` and `category`, and the material's `elements` are saved.

The audit is that of an inline element (media `create`, material `update` of `elements`, the person and channel from
the token). The element is governed like any other: `/media` is denied by `GovernedMediaMiddleware`, downloads use the
signed download gate, and purge removes the media item and its file with the material. The person's recorded
confirmation stays in the platform; the content layer keeps its id only in the signed evidence.

Prerequisites: the platform writes into the storage the media provider reads (for Azure: the media container, blob
name below the provider's root path), keeps the container private and gives the uploader no write access after
verification. Keep `SigningSecret` in a secret store, with the same value in host and platform, and rotate it in both.

## Review feedback, resubmission and archive

The Compliance Pack moves the status; this package lets the owner act on it.

- **Feedback.** `MaterialDetail.review` holds the latest review decision for callers who see the draft: `decision`,
  `comment`, `findings` (for a rejection in Umbraco Workflow: the reviewer's comment), `reviewerName`, `decidedUtc`, and
  `currentVersion` (false once the owner changed the draft after the decision). A rejection moves the status to
  `rejected`, which makes the material editable again for its owner (`update_material`, `add_elements`, groups, links).
- **Resubmit.** `submit_material` / `POST /{key}/submit` with `{ "version" }` runs the Compliance Pack's required-field
  check and sets status `submitted` in one save under the content write lock. An empty required field is refused with
  `invalid` (REST: `400` problem with `code: "invalid"`; MCP: tool error `invalid: ...`). The text names each field with its
  label from the tenant profile (`Limescape:AgentApi:Content:Fields:<alias>:Labels`, in English here), else the property
  name, else the alias; a backoffice localization key (`#lsObjectModel_...`) never appears. The REST problem also carries
  `missingFields: [ { "alias", "label", "culture", "labels": { "nl", "en", ... } } ]` (the MCP error text ends with the same
  JSON), so a client shows the label in its own language without reading the text.
  The review then happens through `approve_material` / `reject_material` (below), in Umbraco Workflow or through the
  Compliance Pack's approval API; all three write the same approval records.
- **Archive.** `archive_material` / `POST /{key}/archive` with `{ "version", "reason" }` calls the Compliance Pack's
  archive route as the person in the token: status `archived`, `archivedAt`, unpublished, retention started, an
  `archive` audit entry with the reason and the channel. The tool is marked destructive and **REQUIRES CONFIRMATION**;
  the `version` is the state the person confirmed (`version_conflict` otherwise). A draft, or an archived material,
  gives `material_not_archivable` (409); someone who is neither an owner with `developer` nor a `functionalManager`
  gets `forbidden` (403); a team or service token `delegated_token_required`. `MaterialDetail.archivedAt` shows the
  archive date to callers who see the draft; readers of the publication get `not_found` afterwards.
- **Owners.** `set_owners` / `PUT /{key}/owners` with `{ "version", "mode", "owners" }` replaces the owners of a material or adds
  to them (co-owners, a new developer, the move to another person key such as the e-mail address in lower case). The owner
  key is whatever the person's token carries (`SubjectClaim`); the operation does not interpret it. It is the only route:
  `owners` is a label property, so a Management API `PUT` of it changes nothing, and `update_material` refuses it. Same
  rules as an update: delegated token, version token, rights checked before anything is written, one save under the
  content write lock, and the Compliance Pack's audit entry for the `owners` field with old and new value, person, channel
  and issuer. An empty list, a key with spaces or a missing `mode` gives `invalid`; the same list again changes nothing
  (no save, no audit entry, same version). Owners are not part of the publication gate's content hash, so changing them
  keeps an approval valid. On a published material only the draft value changes (the published version keeps the old
  owners until the next publication), so the operation is for a `functionalManager` there.
- `status` and `archivedAt` stay out of `update_material` (`invalid`). A Management API `PUT` of these label fields
  changes nothing (see the Compliance Pack's lifecycle section).

## Review and retention management

People work in the Navigator, not in Umbraco. The reviewer's queue and decision
and the functional manager's retention work are operations of this package, with the same rules as Umbraco Workflow and
the Compliance Pack's backoffice screens, checked server-side before anything is written. Every write needs the person's
own delegated token and the `version` the person confirmed, runs under Umbraco's content write lock, and is audited by the
Compliance Pack with the person (`actorId`, `actorName`), the channel (`app` for REST, `assistant` for MCP) and the issuer
of the token. The MCP tools say **REQUIRES CONFIRMATION**; the agent manifest puts them under human-in-the-loop.

### Review

`list_review_queue` / `GET /review-queue` returns, for a `reviewer`, the materials with status `submitted` or `inReview`
that the person does not own, oldest first; for every other role an empty list (a person without any role gets
`forbidden`). One item:

```json
{ "key": "…", "title": "Medicatierekenen in de wijk", "materialType": "lsDocument", "collectionKey": "…",
  "status": "submitted", "owners": ["a.person@example.org"], "version": "2026-10-13T09:01:27.3826257Z",
  "updatedUtc": "2026-10-13T09:01:27.3826257Z", "detailLink": "https://navigator.example.org/materiaal/…",
  "review": null }
```

`review` is the latest earlier decision (for example the feedback of a previous rejection), as in `MaterialDetail.review`.

`approve_material` / `POST /{key}/approve` with `{ "version", "comment" }` and `reject_material` / `POST /{key}/reject`
with `{ "version", "feedback" }`. The checks, in this order (the first that fails answers):

| Check | Code | REST |
|---|---|---|
| a delegated person token | `delegated_token_required` | 403 |
| the `reviewer` role (checked before the material, so a non-reviewer learns nothing about it) | `forbidden` | 403 |
| `version` present; for a rejection `feedback` not empty and at most 10,000 characters; a comment at most 2,000 | `invalid` | 400 |
| the material exists and the person may see it (a plain reviewer sees submitted and in-review drafts and publications in their audience; an unknown key and a hidden material give the same answer) | `not_found` | 404 |
| four eyes: the person is not an owner | `own_material` | 403 |
| status `submitted` or `inReview`, not archived (a draft for a reviewer who can see it, a rejected or published material) | `material_not_reviewable` | 409 |
| still at the confirmed `version` | `version_conflict` | 409 |
| the Compliance Pack or Umbraco refused to record or to publish | `operation_cancelled` | 409 |

On success both answer 200 with a `ReviewDecision`:

```json
{ "materialKey": "…", "decision": "approved", "status": "published", "published": true,
  "reviewerId": "r.reviewer@example.org", "reviewerName": "R. Reviewer", "channel": "assistant",
  "comment": "Helder en volledig.", "feedback": null, "versionHash": "…", "decidedUtc": "2026-10-13T09:02:00Z" }
```

What it writes is what Umbraco Workflow writes, so the publication gate is unchanged:

- **Approve.** The Compliance Pack's `IApprovalService.DecideAs` appends an `approved` record for the current draft's
  hash (source `integration`, the reviewer from the token, the channel of the route, the comment) with an `approval`
  audit entry; then the material is published through Umbraco, so the publication gate checks that record for exactly
  this draft and sets status `published`. Both happen in one scope: a refused publish (for example an unpublished
  collection) records nothing (`operation_cancelled`). Owner rule twice: here (`own_material`) and in `DecideAs`
  (`RequireDifferentReviewer`).
- **Reject.** A `rejected` record with the feedback as findings (as Workflow stores the reviewer's comment), the
  `approval` audit entry, and status `rejected` through the Compliance Pack's rejection route, so the owner reads the
  feedback in `MaterialDetail.review`, changes the material and resubmits it with `submit_material`. Nothing is published.
- **Workflow** in the backoffice keeps working and writes the same records (source `workflow`). The Compliance Pack's
  own approval API keeps its rule that an acting-user token decides only through channel `app` or `api`; this package
  checks the reviewer role from the person's token itself and also accepts channel `assistant`, because the MCP tool runs
  only after the person confirmed it (human-in-the-loop) and the channel is recorded.

### Retention

`list_retention` / `GET /retention?withinDays=30&state=expiring&skip=0&take=100` (a `functionalManager`, else
`forbidden`; out-of-range values `invalid`):

```json
{ "asOfUtc": "…", "withinDays": 30,
  "classes": [ { "key": "…", "name": "Toetsmateriaal", "description": null, "years": 10, "months": 0, "days": 0,
                 "startEvent": "archived", "endAction": "allowDeletion" } ],
  "total": 1,
  "items": [ { "materialKey": "…", "title": "…", "status": "archived", "published": false, "version": "…",
               "state": "expiring", "classKey": "…", "className": "Toetsmateriaal",
               "startDate": "2016-10-20T00:00:00Z", "retentionUntil": "2026-10-20T00:00:00Z", "endAction": "allowDeletion",
               "heldFromEarlier": false, "legalHold": false, "detailLink": "…" } ] }
```

`items` are the materials (not in the recycle bin) whose period has started: `ended` (end date passed: deletion is
allowed unless `legalHold`), `expiring` (ends within `withinDays`) and `running` (later), earliest end first. A material
whose class waits for its start event (not archived yet) has no end date and is not listed; `set_retention_class` returns
it with state `pending`. `startEvent` is `archived`, `published`, `lastModified` or `dateProperty`; `endAction`
`allowDeletion` or `notify`.

`set_retention_class` / `PUT /{key}/retention-class` with `{ "version", "classKey" }` assigns the class to this one
material (an item assignment of the Compliance Pack) in place of the class assigned to it before, and returns the
material's `MaterialRetention` (the item shape above, state computed with a 30-day window). Retention is not a field of
the material, so its `version` stays the same. A shorter class never shortens a period that runs already: only an
administrator confirms that, in the backoffice; the answer then says `heldFromEarlier: true` with the earlier end date.
An unknown class gives `invalid` (400). Creating and changing classes stays in the backoffice.

`set_legal_hold` / `POST /{key}/legal-hold` and `clear_legal_hold` / `POST /{key}/legal-hold/clear` with
`{ "version", "reason" }` set or lift the Compliance Pack's legal hold of one material and return its `MaterialRetention`.
In the backoffice only administrators may do this; through this package the `functionalManager` role does. The audit entry
is the Compliance Pack's (`retentionChange`, field `legalHold`, old and new value with the reason). A repeated call
changes nothing and writes no entry. A reason longer than 500 characters gives `invalid`.

Order of the checks for the three writes: `delegated_token_required` (403), `forbidden` without `functionalManager`
(403, before the material is looked at), `invalid` for the request itself (400: no `version` or `classKey`, a reason
that is too long), `not_found` (404), `version_conflict` (409), and for `set_retention_class` an unknown class (`invalid`, 400).

## Groups and typed links

A group (`lsMaterialGroup`) is an ordered set of materials; a typed link is an Umbraco relation
of type `lsRelated` (both ways), `lsPartOf` or `lsVersionOf`. Umbraco's Management API has no relation write endpoint,
so these writes go through this package with the person's own token, like `update_material`.

- **Direction.** Seen from the material in the call: `add_relation(M, lsPartOf, T)` stores "M is part of T" (relation
  parent T, child M), `lsVersionOf` likewise "M is a version of T". With `inverse: true` it is the other way round ("T is
  part of M", "M has version T"). For `lsRelated` the direction is ignored and an existing link in either direction counts.
- **Rights.** The material in the call must be changeable by the caller (same rules as `update_material`); the other
  material only has to be visible, so a draft can be linked to a published material. A material the caller may not see
  gives `not_found`, with the same message as an unknown key. A published material gives `material_not_editable`.
  Groups have no audience of their own: every role can list them; changing membership changes the group document.
- **Writes.** One scope under Umbraco's content write lock: rights, the current members or links and the write are one
  step, and a repeated call changes nothing (no save, no audit entry). Membership is one save of the group's `members`
  property; a link is `IRelationService.Relate` or `Delete`. No version token: membership and links are not fields of
  the material and do not change its `version`.
- **Reading.** `MaterialDetail.relations` lists the links to materials the caller may see, from both ends: `type`,
  `inverse` (true when this material is the relation parent), `materialKey`, `title` (from the version the caller sees).
  `hiddenRelationCount` counts the others per type. `MaterialDetail.groups` lists the groups that contain the
  material, with the visible `members` in group order and `hiddenMemberCount` for the others. Trashed materials are
  neither listed nor counted.
- **Notifications.** A membership change is a save of the group (`Limescape.ContentSaved` for the group in the content
  layer host). A link change fires Umbraco's relation notifications; the host sends `Limescape.RelationSaved` or
  `Limescape.RelationDeleted` for both materials.

## REST endpoints

Under `RestBasePath`, same token, same rights; errors as `application/problem+json` with the code in `code`.

| Method and path | Operation | Errors |
|---|---|---|
| `GET /collections` | list collections | 401, 403 |
| `GET /metadata-context?materialType=` | metadata context; `materialType` limits it to one type (and the collections that allow it) | 401, 403, 404 (unknown `materialType`) |
| `POST /` | create material (body as the MCP arguments); 201 | 400, 401, 403, 409 |
| `GET /{key}` | material | 401, 404 |
| `PUT /{key}` | update material (body as the MCP arguments without `materialKey`); 200 with the new `version` | 400, 401, 403, 404, 409 (`version_conflict`, `material_not_editable`) |
| `POST /{key}/submit` | submit for review: `{ "version" }`; 200 with the material | 400 (required fields), 401, 403, 404, 409 (`version_conflict`, `material_not_editable`) |
| `POST /{key}/archive` | archive: `{ "version", "reason" }`; 200 with the material | 400, 401, 403, 404, 409 (`version_conflict`, `material_not_archivable`) |
| `POST /{key}/elements` | add elements (body: array of elements) | 400, 401, 403, 404, 409 |
| `DELETE /{key}/elements/{elementKey}?version=` | remove one element; the `version` of the material as last read is a query parameter; 200 with the material without the element | 400 (no `version`), 401, 403, 404, 409 (`version_conflict`, `material_not_editable`, `operation_cancelled`) |
| `POST /{key}/verified-uploads` | add the file of a signed upload session as element (body: the session); 200 with the material, also for a retry | 400, 401, 403 (`upload_session_invalid`, rights), 404, 409 (`material_not_editable`, `upload_session_used`), 501 |
| `GET /{key}/detail-link` | detail link | 401, 404, 501 |
| `GET /groups` | groups | 401, 403 |
| `PUT /{key}/groups/{groupKey}` | add to group; 200 with the material | 401, 403, 404, 409 |
| `DELETE /{key}/groups/{groupKey}` | remove from group; 200 with the material | 401, 403, 404, 409 |
| `PUT /{key}/relations/{type}/{targetKey}?inverse=false` | create link; 200 with the material | 400 (type, self link), 401, 403, 404 (material or target), 409 |
| `DELETE /{key}/relations/{type}/{targetKey}?inverse=false` | remove link; 200 with the material | same |
| `GET /review-queue` | review queue; 200 with `ReviewQueueItem[]` (empty for non-reviewers) | 401, 403 (no role) |
| `POST /{key}/approve` | approve and publish: `{ "version", "comment" }`; 200 with a `ReviewDecision` | 400, 401, 403 (`forbidden`, `own_material`, `delegated_token_required`), 404, 409 (`material_not_reviewable`, `version_conflict`, `operation_cancelled`) |
| `POST /{key}/reject` | reject: `{ "version", "feedback" }`; 200 with a `ReviewDecision` | same; 400 without feedback |
| `GET /retention?withinDays=&state=&skip=&take=` | classes and periods; 200 with a `RetentionOverview` | 400, 401, 403 |
| `PUT /{key}/retention-class` | assign a class: `{ "version", "classKey" }`; 200 with a `MaterialRetention` | 400, 401, 403, 404, 409 |
| `POST /{key}/legal-hold` | set the legal hold: `{ "version", "reason" }`; 200 with a `MaterialRetention` | 400, 401, 403, 404, 409 |
| `POST /{key}/legal-hold/clear` | lift the legal hold: `{ "version", "reason" }`; 200 with a `MaterialRetention` | same |
| `PUT /metadata-fields/{alias}/state` | archive or restore a field and set its deprecated codes: `{ "version", "archived", "deprecatedValues" }`; 200 with a `MetadataFieldState` | 400, 401, 403 (`forbidden`, `delegated_token_required`), 409 (`version_conflict`) |

## Element content for the index sync

`GET {RestBasePath}/{key}/elements/{elementKey}/content` returns the bytes of one element to a trusted server-side
integration, such as the index sync of the platform that extracts and indexes the text of PDF and DOCX elements. It is
**not** in the user-token group above:

- **Authentication.** The backoffice bearer of an Umbraco **API user** (client credentials, the same token as on the
  Management API), authorized by Umbraco's `SectionAccessContent` policy, plus a valid `X-Limescape-Acting-User` token
  (validated by the Compliance Pack against `Compliance:ActingUser:TrustedIssuers`). A person token of the Agent API is
  401 (another scheme); no or an invalid acting-user header is 401 (`acting_user_required`, `acting_user_invalid`); a
  backoffice user without the content section is 403; a backoffice user that is not an API user is 403
  (`api_user_required`).
- **Membership.** The element belongs to the material when it is in the elements of the working version **or** of the
  published version. There is no person, so no person visibility: drafts are included. The material must be a material
  (composition of `MaterialCompositionAlias`), the media must be of `ElementMediaTypeAlias`, and neither may be in the
  recycle bin.
- **200.** The content layer streams the bytes itself (never a blob URL or SAS), with `Content-Type` from the extension
  (as the detail-page downloads), `Content-Disposition: attachment; filename*=UTF-8''...`, `Cache-Control: no-store,
  private`, `X-Content-Type-Options: nosniff` and `X-Limescape-Element-Sha256` with the stored SHA-256 when one is stored.
- **403 `element_confidential`.** The element has a true `confidential` property. The problem body carries no file name,
  size or content.
- **404 `element_not_found`.** Unknown material or element, an element of another material, media of another type, or a
  material or element in the recycle bin.
- **Logging.** No audit entry and no log line for a successful read (no audit noise per sync); one warning per refusal
  with material key, element key, acting-user id and correlation id, never the file name. A person token is refused by
  authentication before the endpoint runs and is therefore not logged by it.

There is no MCP equivalent: file bytes do not belong in an MCP tool result.

## Detail page and downloads

The host renders `/materiaal/{key}` with `MaterialPageService` from this package: the same
`ILearningMaterialService.GetMaterial` visibility check, document-type metadata groups, version identifiers/dates,
and individually visibility-filtered relations. Related materials the viewer may not see are shown as an anonymous
count per relation type ("2 related materials not accessible to you"), without key, title or any other trait;
the same count is `hiddenRelationCount` (type alias to number) in `MaterialDetail` over REST and MCP. Trashed related
materials are not counted, another person's draft and group-restricted publications are. History never returns old
field values. Trashed materials and media are hidden. Elements with a Boolean `confidential` property are returned only
to material owners and functional managers.
`MemberCaller.FromPrincipal` maps a validated frontend member identity using the **same** `Authentication:RoleMapping`
and subject/name/role claim configuration as bearer callers. Member sessions are read-only, with no delegated scope
inferred from an ID token. The host owns the Razor views, OIDC session and routes; rules and downloads stay here.

| `Limescape:AgentApi:DetailPage` key | Default | Purpose |
|---|---|---|
| `Enabled` | `false` | Tenant feature flag for the host page and signed downloads |
| `DownloadLifetimeSeconds` | `600` | Between 1 and 600 seconds, validated on startup |
| `BlobServiceUri` | empty | HTTPS account endpoint, e.g. `https://account.blob.core.windows.net`; no connection string/SAS |
| `BlobContainer` | `media` | Must match the Umbraco media provider container |
| `RelationTypes` | `lsRelated`, `lsPartOf`, `lsVersionOf` | Relations to display and the only types `add_relation`/`remove_relation` accept (other built-in CMS relations are omitted) |

Azure downloads use a **user-delegation** key via `DefaultAzureCredential` and a read-only, HTTPS SAS for exactly one
blob. Grant the host managed identity blob read access and `generateUserDelegationKey` at account scope. Keep the
container private (`allowBlobPublicAccess: false`, container `publicAccess: None`).
There is no account-key SAS fallback. A configured Azure media provider requires `BlobServiceUri` in this host.
See [Microsoft's user-delegation guidance](https://learn.microsoft.com/azure/storage/blobs/storage-blob-user-delegation-sas-create-dotnet).

Locally, `SignedDownloadService` returns `/materiaal/download?ticket=...`, protected with ASP.NET Data Protection.
The ticket binds the person, material, element, expiry and inline/attachment disposition. The download endpoint checks
expiry, the current member/bearer identity, current material visibility and element membership again, and streams
through `MediaFileManager` with range support. HTML/SVG/unknown formats are download-only. PDF/audio/video can be inline.
Both page and downloads use `no-store` and `no-referrer`. Persist/share the Data Protection key ring across host replicas.
NL/EN WebVTT captions are elements named `<video-stem>.nl.vtt` / `<video-stem>.en.vtt`; they use separately authorised
inline `text/vtt` links. A plain-text `transcript` property on an element is rendered as an expandable transcript.
For Azure captions, configure Blob CORS for the Navigator origin (GET/HEAD and required range headers); the video requests
use anonymous CORS with SAS, while local same-origin requests still carry the member session. Uploaded media with speech
must include captions/transcripts in the release content check.

The host installs `GovernedMediaMiddleware` **before** Umbraco/static media middleware. It denies `/media` access for
`ElementMediaTypeAlias` and unknown media variants, so nobody bypasses the signed endpoint via original media URLs.
Known ordinary CMS media remain available. A consuming host must install this middleware before its static/media pipeline,
including when the page feature is disabled if governed elements already exist. Blob public access must remain disabled.

The host smoke tests cover both locales, member cookies, an OIDC code/PKCE challenge, neutral 404, relations,
forged/person-mismatched/expired links and `/media` bypass. Package tests inspect the generated delegation SAS and reject
lifetimes over ten minutes. Browser tests in `tests/detail-page-accessibility` run axe-core and keyboard checks on the real
Razor output exported by the host test, including native media controls, 320 px and 200% text size. These do not replace
the live Navigator rewrite/IdP round trip or a screen-reader audit.

## Audit

Writes run as Umbraco's default user inside one Umbraco scope and go through `IContentService` and `IMediaService`.
This package writes no audit entries itself: the Compliance Pack's audit handlers record every save, with field-level
diffs, attributed to the person this package sets as acting user. Creating a material gives `create` entries for the
document (one for the item, one per filled field), `create` entries per element media item and `update` of the
material's `elements`; `update_material` gives one `update` entry per changed field with the old and new stored value
(list fields as their JSON array) and one for `@name` when the title changes. Group membership gives one `update` entry
for the group's `members` field; a link gives `relate` or `unrelate` on both materials (field `@relation.<alias>`). All with channel `assistant` (MCP) or `app` (REST). The same request gives the same audit entries
on both routes, apart from the channel. The `issuer` field of every entry is the `iss` of the token: the identity
provider, or the trusted issuer of a delegated person token. Review and retention operations use the Compliance Pack's
own entries: `approval` (and `publish`, or `update` of `status` for a rejection), `retentionChange` for a class
assignment and for the field `legalHold`.

## Build, test and package

From the repository root:

```sh
dotnet build --configuration Release
dotnet test --project tests/Limescape.Umbraco.AgentApi.Tests --configuration Release
dotnet pack src/Limescape.Umbraco.AgentApi --configuration Release --output artifacts/agentapi
```

Until `Limescape.Umbraco.Compliance` is available on NuGet, pack it from its own repository into a local folder and add
`-p:RestoreAdditionalProjectSources=<folder>` to the build command.

The package tests run the real endpoint filter in ASP.NET TestServer (authentication, transport, tool list) without a
CMS. The tests named below that run against a real Umbraco site on SQLite are part of the Limescape content layer host
and are not in this repository. There the service layer is tested (`AgentApiTests`):
MCP and REST give the same material and audit entry, and every write is refused without the developer role, with a
team or service token, with system fields (status, owners), and on an archived material under retention.
`UpdateMaterialTests` cover the update over REST and MCP: equal result and audit entries (also read back through the
Compliance Pack's `GET compliance/audit`), a stale version (409), four
concurrent updates with one version (exactly one wins), not visible (404), not the owner (403), published (409), system
fields (400) and the kind and link rules. `GroupRelationTests` cover groups and links over REST and MCP: idempotent add
and remove, both directions, audit on both materials, a hidden target (404 without any trace, nothing written), not the
owner and a lost developer role (403), anonymous (401), a team token (403), a published material (409) and invalid input.
`UploadSessionTests` cover verified upload sessions over REST and MCP: the element points at the existing file (no
copy), audit, `/media` denied, an idempotent retry; and, calling the content layer directly, another secret, a field
changed after signing, a signature for another material, no signature and an expired or too long signature (403), a
SHA-256 that differs from the stored file, above the category maximum, size mismatch, a path outside the prefix or with
`..`, a missing file, an unknown category and a malformed checksum (400), a published material (409), a file claimed by
a second material (409), another developer (404), a team token (403) and anonymous (401).
`LifecycleTests` cover the rejection feedback, an update and `submit_material` after a rejection, `archive_material` over
MCP and REST (status, date, unpublish, OAI-PMH tombstone, retention start, audit) and its refusals: not an owner (403), a
draft (409), a stale version (409), a team token (403), plus a Management API `PUT` of the label fields that changes nothing.
`DelegatedTokenTests` (package tests, no CMS) cover delegated person tokens over MCP and REST: accepted with the
person, issuer and roles mapped, identity-provider tokens unchanged next to it, refused with 401 (HS256, unsigned,
another key, unknown issuer, expired, 601 seconds, issued in the future, wrong or missing `aud`, no `iat`, no `sub`,
invalid `channel`), 600 seconds accepted, off without configuration, the Compliance Pack fallback with the Agent API
audience, per-issuer audiences and startup validation; the host `DelegatedTokenTests` prove the audit (person, channel,
platform as issuer, also through `GET compliance/audit`), the owner rule across both token routes, 403 without roles and
401 outside the contract against the real host.
`ReviewAndRetentionTests` cover review and retention over REST and MCP: approve (record as Workflow writes it, published
through the gate, audit with person, channel and issuer, gone from the queue), reject with feedback read by the owner and
the gate still closed; and, calling the content layer directly, an owner who is also a reviewer (403 `own_material`), no
reviewer role (403), a rejection without feedback (400), a draft or a published material (409), a draft hidden from a
plain reviewer and an unknown key (the same 404), a stale version (409), a team token (403), an empty queue for reader,
developer and manager; list, class assignment (a shorter class keeps the longer period), expiring and ended periods,
legal hold set, repeated and lifted with audit, and for retention a developer or reviewer (403), an unknown key (404), a
stale version (409), an unknown class and a long reason (400) and a team token (403), each with nothing written.

Dependency versions are managed centrally in `Directory.Packages.props`. The Compliance Pack is a NuGet dependency
(`Limescape.Umbraco.Compliance`).

## Releases

Changes per version; see also [CHANGELOG.md](CHANGELOG.md).

### 1.0.0

- First public version. It contains everything listed below for the development versions 0.1.0 up to and including 0.11.0, plus the optional import facts.

### 0.11.0

- Review and retention management from the Navigator: `list_review_queue`, `approve_material`,
  `reject_material`, `list_retention`, `set_retention_class`, `set_legal_hold`, `clear_legal_hold` (MCP) and the same
  over REST; new error codes `own_material` (403) and `material_not_reviewable` (409). `approve_material` is the first
  tool that publishes, always through the reviewer's approval record and the publication gate.
- Needs the Compliance Pack with `IApprovalService.DecideAs`.
- `GET /{key}/elements/{elementKey}/content` for the index sync: the API user of the tenant with an acting-user
  token reads the bytes of an element of the working or published version; 403 `element_confidential`, 404
  `element_not_found`; streamed by the host with no-store headers and the stored SHA-256; refusals logged, reads not.

### 0.10.0

- `set_owners` (MCP) and `PUT /{key}/owners` (REST): replace or add the owners of an existing material, with version token,
  the rights of an update, audit and no change to a published (without `functionalManager`) or archived material.

### 0.9.0

- Delegated person tokens of a trusted issuer next to identity-provider tokens: options `UserToken:TrustedIssuers`
  and `UserToken:Audience` (fallback to `Compliance:ActingUser:TrustedIssuers`), asymmetric signature, `aud` required,
  lifetime at most 600 seconds, same `AgentUser` mapping and rules; the audit shows the issuer. Off by default.
- Needs the Compliance Pack with `TrustedIssuerTokenValidator` (shared with the acting-user header).

### 0.8.0

- `submit_material` (MCP) and `POST /{key}/submit` (REST): resubmit a draft or rejected material after the required-field
  check.
- `archive_material` (MCP) and `POST /{key}/archive` (REST) through the Compliance Pack's archive route; error
  `material_not_archivable` (409).
- `MaterialDetail.review` (latest decision with feedback) and `MaterialDetail.archivedAt`, for callers who see the draft.
- Needs the Compliance Pack with the lifecycle routes (`IArchiveService`, rejection status).

### 0.7.0

- Verified upload sessions: REST `POST /{key}/verified-uploads` and MCP `add_element_from_session`
  register a platform-signed (HMAC-SHA256) session file as an element without copying it, with the rights of
  `add_elements`, category limits (`UploadSessions:CategoryMaxBytes`), size and SHA-256 checks against the stored file,
  and idempotent retries under the media and content write locks.
- Options `UploadSessions:*`; error codes `upload_session_invalid` (403) and `upload_session_used` (409).
- `LearningMaterialService` takes a `TimeProvider`.

### 0.6.0

- Groups and typed links : MCP `list_groups`, `add_to_group`, `remove_from_group`, `add_relation`,
  `remove_relation` and REST `GET /groups`, `PUT`/`DELETE /{key}/groups/{groupKey}`,
  `PUT`/`DELETE /{key}/relations/{type}/{targetKey}`, with the rights of `update_material` on the material and
  visibility on the target.
- `MaterialDetail.relations` and `MaterialDetail.groups`; the detail page reads its relations from `MaterialDetail`.
- `Content.GroupDocumentTypeAlias`, `Content.GroupMembersProperty`.
- Needs the Compliance Pack with relation audit (`relate`, `unrelate`).

### 0.5.0

- Publication-only readers receive published property values, including audience, owners, kind, URL and element selection. Owners, functional managers and reviewers of submitted/inReview materials receive draft values.
- Group-shared publications require a matching validated `groups` claim enabled in `Authentication.RoleClaims`; app role values are never membership. Missing or malformed audience data fails closed.
- `Content.SharingGroupsProperty` defaults to `sharingGroups`, a JSON array of group ids installed by host migration `ls-object-model-004-sharing-groups`. Create and `update_material` through `metadata.sharingGroups` (JSON string); `MaterialDetail.sharingGroups` returns the complete list.
- REST, MCP, member detail pages and download authorization share the same service.
- `get_material` (MCP), the same read as `GET /{key}`. `version` is only returned to callers who see the draft; publication-only readers get no version token.
- `update_material` applies the same visibility first (a hidden material is `not_found`), then the 0.4.0 rights and editability rules on the draft values.
- `metadata.sharingGroups` is validated on create and update (JSON array of non-empty ids).

### 0.4.0

- `update_material` (MCP) and `PUT /{key}` (REST): metadata changes with optimistic concurrency on `version`, checked
  under the content write lock; error `version_conflict` (409). Same rights as `add_elements`.
- `MaterialDetail.version`. Published materials are no longer editable through `add_elements` either.
- No audit writes in this package any more: the Compliance Pack's handlers log every save with field-level diffs.

### 0.3.0

- Read-only member caller using the same subject/role mapping as bearer access.
- Material page data from document-type groups, lightweight version history and visibility-filtered relations.
- User-delegation SAS and local person-bound download tickets, maximum ten minutes; visibility checked again locally.
- Governed-media bypass guard; trashed materials and confidential elements are hidden server-side.
- Host NL/EN Razor page, PDF/audio/video previews, WebVTT captions and transcript support; browser accessibility tests.

### 0.2.0

- User-token authentication (OIDC discovery, audience, delegated scope, role mapping); team and service tokens are read-only.
- `LearningMaterialService` with collections, metadata context, create (draft), add elements, detail and detail link.
- MCP tools `list_collections`, `propose_metadata_context`, `create_material`, `add_elements`, `get_detail_link`.
- REST endpoints over the same service; acting person and channel recorded in the Compliance Pack audit log.

### 0.1.0

- Opt-in configurable MCP endpoint through an Umbraco composer.
- Stateless Streamable HTTP with an empty tool list.

## Optional import facts

`create_material` and `update_material` (REST and MCP) accept `originalDate` (ISO date only, never after UTC today) and `provenance` (trimmed text, max 500). Both are optional, never required by the default submission rule. Send them in `metadata` or as named arguments, never both. `source` remains the citation. `get_material.fields` and the canonical detail page include them.

Only a functional manager may update or clear these facts after creation; other metadata can still be changed by the developer. Omission/null leaves a value unchanged; an empty string clears it. Existing confirmation, version and lifecycle rules apply. `propose_metadata_context` returns both with `target: metadata`, `required: false`, `proposable: false`; an AI proposal must exclude them. `originalDate` uses `Umbraco.DateOnly` with `type: date`; `provenance` uses `Umbraco.TextArea` with `type: text`. Tenant profile labels supply nl/en/en-GB.

Errors: `original_date_invalid`, `original_date_future`, `provenance_too_long` (REST 400), `import_metadata_manager_required` (403). They are also enforced on direct content saves, including Management API writes. A backoffice manager needs a user-group alias in `Authentication:RoleMapping:functionalManager`; an API account alone never grants this right. The full downstream contract is [import-metadata.md](../../specs/012-bulkimport/contracts/import-metadata.md).

## Licence

Copyright © 2026 TrueLime B.V. Source available under the Limescape Open Platform Licentie (LOPL), versie 1.0, see [LICENSE](LICENSE). The licence text is in Dutch and the Dutch version prevails. The LOPL is a source-available licence, not an OSI-approved open-source licence.

TrueLime B.V. grants the rights under the LOPL to public organisations and other non-profit organisations that register as a user at https://www.limescape.ai and accept the licence. Any other use, such as commercial hosting, managed services, OEM or white-label use, requires a commercial licence from TrueLime B.V.
