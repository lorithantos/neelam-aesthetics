# Neelam Aesthetics — campaign safety

Email campaigns go out through an editor with no API, so mistakes can't be caught by
automation on the sending side. This project moves the email upstream: it is written as
**structured data**, checked by a **safety gate**, and only then **exported as blocks** that a
person pastes into the editor in order.

```
Campaign (typed data) ──► CampaignReview.Check ──► blockers? ──yes──► no export
                                                      │
                                                      no
                                                      ▼
                                       EditorExport.Blocks / PlainText
```

## Why: the one-year / Beauty Bank email

Each rule exists because of something in that email. `SampleCampaigns.AsSent()` in the tests
reproduces it, and the tests pin which rules it trips.

| Problem in the sent email | Rule | Severity |
|---|---|---|
| Both tiers named "Platinum Member" | `tier-names-unique` | Blocker |
| "50% Complimentary Wellness Injections" — free or half off? | Not expressible: benefits are typed (`FreeItem` / `DiscountedItem`) and worded by the model; `benefit-value` rejects 100%-off | Blocker |
| No link or button to join | `cta-required`, `cta-https` | Blocker |
| Monthly charge with no cancellation / rollover / refund terms | `terms-required` | Blocker |
| Promotes wellness injections with no disclaimer | `medical-disclaimer` | Blocker |
| "Bank", "savings account" for a prepaid service plan | `restricted-term` — needs sign-off, possibly counsel | Warning |
| "100% of your money goes to…" three times | No per-tier slot for it (`TiersNote` is said once); `repeated-phrase` catches free text | Warning |
| Tier 2's injection benefits shaped differently from tier 1's | `tiers-parallel` | Warning |
| "Beautiful🤍", "✨The" | `emoji-spacing` | Warning |
| Heavy emoji use | `emoji-budget` | Warning |

Restricted terms are warnings, not blockers, on purpose: whether "Beauty Bank" is acceptable is
a business and legal call, not one for the tool to make.

## Layout

- `src/Neelam.Campaigns` — the model (`Campaign`, `Offer`, `Tier`, `Benefit`), the gate
  (`CampaignReview`, tunable via `CampaignPolicy`) and the export (`EditorExport`). No web,
  storage or hosting dependency, so it works whichever editor and stack are chosen.
- `tests/Neelam.Campaigns.Tests` — the sample email as sent and as corrected, plus one test
  per rule.

```
dotnet test
```

## Open decisions

1. **Which editor** — the sample's footer is Square Marketing's; Squarespace Email Campaigns is
   a different editor. `EditorBlock` kinds (heading, text, list, button, divider) map onto
   either, but personalisation tokens (first name) differ and are not modelled yet.
2. **Web app** — proposed: Blazor Server on Azure App Service, Entra ID sign-in, Azure SQL for
   templates, sent campaigns and approvals.
3. **Approval** — one person, or a second approver required before export? And must warnings be
   acknowledged individually before export, or only blockers stop it (current behaviour)?
4. **Policy values** — restricted terms, medical terms and the emoji limit in `CampaignPolicy`
   are starting points for the owner to set.
