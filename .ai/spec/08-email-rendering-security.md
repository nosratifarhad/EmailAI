# 08 - Email rendering and security

**Purpose.** Render the mail users actually receive (HTML) while guaranteeing that a message can
never script the application, exfiltrate data, or break the session.

**Scope.** HTML body rendering, the sanitiser, the sandbox document, CSP, plain-text fallback,
link/URL handling. Sending mail is out of scope (that is the reply endpoint).

**Inputs.** `EmailMessage.BodyHtml` / `BodyText` from Exchange (untrusted). A message may have
HTML only, text only, both, or neither.

**Outputs.** A sanitized HTML document rendered inside a sandboxed, script-less `<iframe>`
(`srcdoc`), with a restrictive Content-Security-Policy, plus a toggle to the extracted plain
text and an explicit "no body" state.

**Responsibilities.** `MailHtmlSanitizer` (element/attribute/URL filtering + sandbox document +
CSP), `MailTextExtractor` (plain-text extraction/fallback), `MailClient.razor` (rendering and the
HTML/text toggle).

**Invariants.**

1. HTML is preferred when present; a message with no HTML falls back to the plain text, and a
   message with neither renders an explicit "no body" state - never a blank pane.
2. The frame is sandboxed **without** `allow-scripts`; the document carries the CSP from
   `MailHtmlSanitizer.SandboxContentSecurityPolicy` (`default-src 'none'`, `script-src 'none'`,
   `object-src 'none'`, `frame-src 'none'`, `form-action 'none'`, `base-uri 'none'`).
3. `script`, `iframe`, `frame`, `frameset`, `object`, `embed`, `applet`, `form`, `meta`, `link`,
   `base`, `input`, `button`, `textarea`, `select`, `option` and HTML comments are removed
   (containers with their content).
4. Every inline event handler (`on*`) is removed.
5. URL attributes are neutralised unless the scheme is `http`, `https`, `mailto`, `tel`, `cid`,
   a fragment/relative URL, or a `data:image/...` (everything else, including `data:text/html`,
   `javascript:`, `vbscript:`, `blob:` and `file:`, is dropped). The check decodes HTML entities
   and control/whitespace obfuscation twice, so `jav&#x09;ascript:` cannot slip through.
6. Sanitisation runs to a stable result (bounded passes), so nesting tricks such as
   `<scr<script>ipt>` cannot re-form a dangerous tag.
7. Links gain `target="_blank" rel="noopener noreferrer"` so an opened page cannot reach back
   into the app.
8. The sanitiser never throws and never drops a whole body because of one bad construct; it is
   defence-in-depth, the sandbox is the primary barrier.

**Failure modes.**

| Failure | Behaviour |
| --- | --- |
| Malformed/partial HTML | Tolerant filtering; the remaining readable text is still shown |
| Dangerous constructs | Removed silently; the rest of the message renders |
| Entirely unrenderable body | Plain-text fallback, then the explicit "no body" state |
| Remote image blocked by the network | Layout may degrade; the text stays readable (nothing is fetched by the app itself) |
| Empty/whitespace HTML | Treated as "no HTML" (`Sanitize` returns an empty string) |

**Security constraints.** Email content is untrusted input. No script execution, no parent-frame
access, no form submission, no dangerous navigation is possible from a message body; the app
window itself only ever navigates within its own origin (foreign links open externally).

**Configuration ownership.** Not configurable - the security posture is fixed in code. There is
no "allow unsafe HTML" switch.

**Implementation.** `src/EmailAI.Application/Mail/MailHtmlSanitizer.cs`,
`src/EmailAI.Application/AI/MailTextExtractor.cs`,
`src/EmailAI.Api/Components/Pages/MailClient.razor`,
`src/EmailAI.Api/wwwroot/app.css` (frame styling).

**Tests.** `MailHtmlSanitizerTests` - script/embed/form removal, inline event handlers, dangerous
and obfuscated URL schemes (including `data:text/html` and `javascript:` variants), comments,
`data:image` retention, link hardening, formatting preservation, the CSP-bearing sandbox
document, and the stable-result behaviour that defeats nested-tag tricks.
`MailTextExtractorTests` - plain-text extraction/fallback.
