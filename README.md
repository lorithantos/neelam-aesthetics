# Neelam Aesthetics — campaign safety

Email campaigns go out through an editor with no API, so mistakes can't be caught by
automation on the sending side. This project moves the email upstream: it is written as
**structured data**, checked by a **two-part gate**, and only then **exported as blocks** that a
person pastes into the editor in order.

```
Campaign ──► CampaignReview (rules: instant, not dismissable)  ─┐
         └─► IProofreader   (Claude: reads it like an editor)  ─┴─► CampaignGate ──► ReviewReport
                                                                                        │
                                                    blockers, or not proofread ◄── no ──┤ CanExport?
                                                                                       yes
                                                                                        ▼
                                                                     EditorExport.Blocks / PlainText
```

- **Rules** catch what can be stated exactly: duplicate tier names, identical tiers, missing
  links, missing terms. A rule blocker cannot be dismissed; it has to be fixed.
- **The AI proofread** catches what a careful reader would: spelling, contradictions, offers
  that don't match their names, placeholder text left in. Its errors block too, but a person
  can dismiss one with a recorded reason and name (`Dismissal`), since a model can be wrong.
- **It fails closed.** If the proofread can't run (outage, refusal, cut off), that is itself a
  blocker. Sending without it takes a named dismissal.
- An AI finding whose quoted excerpt isn't actually in the email is downgraded to a warning
  rather than trusted.

## Why: the one-year / Beauty Bank email

It went out twice. `SampleCampaigns.FirstSend()` and `SecondSend()` in the tests reproduce
both, and the tests pin which rules each trips.

| Problem in the sent email | Rule | Severity |
|---|---|---|
| First send: both offers identical | `tier-content-distinct` | Blocker |
| Second send: one offer fixed, both still named "Platinum Member" | `tier-names-unique` | Blocker |
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

- `src/Neelam.Campaigns` — the model (`Campaign`, `Offer`, `Tier`, `Benefit`), the rules
  (`CampaignReview`, tunable via `CampaignPolicy`), the gate (`CampaignGate`, `IProofreader`)
  and the export (`EditorExport`). No web, storage, hosting or AI dependency.
- `src/Neelam.Campaigns.Claude` — `ClaudeProofreader`, the `IProofreader` backed by Claude
  (Anthropic C# SDK, structured JSON output). Reads `ANTHROPIC_API_KEY` by default.
- `tests/Neelam.Campaigns.Tests` — both real sends and a corrected version, one test per rule,
  the gate with a fake proofreader, and parsing of Claude's answer. No test calls the network.

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
4. **Where Claude runs** — directly against the Anthropic API, or through Microsoft Foundry
   inside the Azure subscription (`AnthropicFoundryClient`, same proofreader code).
5. **Policy values** — restricted terms, medical terms and the emoji limit in `CampaignPolicy`
   are starting points for the owner to set.
