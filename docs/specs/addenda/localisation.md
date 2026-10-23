# Addendum: languages and localisation

**Proposed, pending business sign-off** (ADR-019). Recorded as OQ-068.

- **Basis:** SRS §1.3 (the platform serves several languages). Static data: `tenant.cfg_locale_defaults` (`languages`), `refdata.ref_languages`, `channel.ref_ui_label_and_translation_bundles`, the `labels` column common to every static data table, `document.cfg_document_templates.language`, `product.cfg_product_template_assignments.language` and `party.individual.preferred_language`.
- **What the specifications leave undefined:** which of a tenant's languages is the default, the form of `labels`, where API message texts live, and how documents choose a language.

## 1. A tenant's languages

- A tenant's **supported languages** are the `languages` list of its `cfg_locale_defaults` record in force.
- The **first entry is the tenant's default language.** *(Proposal: the catalogue has no separate default field.)*
- A tenant with no locale defaults in force supports English only, and its default is English.
- Language codes are ISO 639-1, two lower-case letters, and must be in `refdata.ref_languages`.

## 2. The language of a request

The API resolves one language for each request:

1. Read the `Accept-Language` header, in order of quality. Only the primary subtag counts: `af-ZA` is `af`. A `*` entry is ignored.
2. The first requested language that the tenant supports is the **request language**.
3. If none is supported, or no header is sent, the request language is the tenant default.
4. A request with no tenant (a 401, for example) uses English.

Text is chosen by a **fallback chain**: the request language, then the tenant default, then English. The first one with a text wins.

Every `/api` response carries `Content-Language`. Normally this is the request language. On a ProblemDetails response it is the language its `title` was taken from.

## 3. Static data labels

- `labels` holds display text only: a JSON object from language code to text, for example `{"en": "Monthly", "af": "Maandeliks", "zu": "Nyanga zonke"}`.
  - Each key must be a two-letter language code, and each value a non-empty string of at most 500 characters.
  - Create, edit and import refuse anything else, with `VALIDATION_FAILED` on `labels`.
- Every static data read returns two more fields:
  - `displayLabel`: the text chosen from `labels` by the fallback chain. If `labels` has none of those languages and the table has its own `label` column, that column's text is used.
  - `displayLabelLanguage`: the language of `displayLabel`. A table's own `label` column counts as English.
  - Both are null when there is no text.
- `labels` itself is still returned whole, for editing.

## 4. API messages

- The message catalogue is `channel.ref_ui_label_and_translation_bundles`. Its `label_key` is the error code catalogue's `message_key`, for example `error.billing.open_invoices`.
  - Global rows serve every tenant, and a tenant's own rows take precedence over them.
- ProblemDetails:
  - `title` is the catalogue text for `<message_key>`, chosen by the fallback chain.
  - `detail` is the catalogue text for `<message_key>.detail` when there is one in the same language. Otherwise the server's own detail is kept, which is English and may name the records involved, and the extension `detailLanguage: "en"` says so.
  - With no catalogue text in the chain, the built-in English title is kept.
  - **The stable `code` never changes with language.**
- English texts need no catalogue rows: the built-in English is the last fallback.
- Catalogue texts are cached per tenant and language for up to 60 seconds.

## 5. Documents

- A document generated for a party uses the template version in the first language of this chain that has one: the party's preferred language, then the tenant default, then English.
- The candidates are the templates assigned to the product for that document type (`cfg_product_template_assignments`) or, for a named template such as a renewal invitation or remittance advice, the template's versions in force.
- If no template is in any of those languages, generation fails as it does today, and the event is retried and then dead-lettered.
- The language used is recorded on the generation request (`document.generation_request.language`) and shown on the document.
- The party is the policyholder for General and Life policy documents and renewal invitations, and the provider for remittance advices. General's `GeneralPolicyEndorsed` and `GeneralRenewalInvited` gain an optional `PolicyholderPartyId`, a backward-compatible change.

## 6. Notifications

Wave 1 notifications use the same chain to choose a template version.

## 7. A party's preferred language

- An individual's preferred language is set at registration, or changed with `PUT /api/v1/party/parties/{id}/preferred-language` `{ "language": "zu" }`. The call needs `party.update` and If-Match.
- Through this endpoint it must be a language in `refdata.ref_languages`. Registration and `PUT …/individual` check only the format: two lower-case letters.
- A language the tenant does not support is accepted, and the chain falls back past it.
- Organisations have no preferred language in the schema (`party.individual.preferred_language` only), so documents for them use the tenant default.

## 8. Sample translations

Workstream F's OFT-ZA tenant supports English (the default), Afrikaans, isiZulu and isiXhosa. Every non-English string it loads is marked **"sample translation, requires professional review"** in the record's `description`, and none is presented as final.
