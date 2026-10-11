# v1 re-entry: the live first-send campaign against the real first send

The live campaign `/campaigns/c3208bbc-...` (label v1) was entered from the old
`BeautyBankEmail.FirstSend()` sample. On 2026-10-09 that sample was checked word for word against the
first send as it went out (2026-10-01 16:00 UTC) and corrected. Below is each field of the campaign
editor that must change for v1 to match the real email, as far as the typed model can say it. Nothing
here has been changed on the live site.

Fields are named as the campaign editor names them (block label, then field).

## Must change

| Block / field | v1 has (old sample) | Change to (as sent) |
|---|---|---|
| Opening, paragraph 2 | "... Your support for our small family business has meant ..." | "... Your support for our small **F**amily business has meant ..." (capital F, as sent) |
| Offer > Name | `✨The Neelam Aesthetics Beauty Bank✨` | `Introducing: ✨The Neelam Aesthetics Beauty Bank✨` (the sent heading was two lines, "Introducing:" then the name; one line is the closest a name holds) |
| Offer > Tier 1 > Name | `Platinum Member` | `Option 1 Platinum Member` |
| Offer > Tier 2 > Name | `Platinum Member` | `Option 2 Platinum Member` |
| Tier 1 and Tier 2 > benefit "Something free" > Item | `wellness injection` | `Wellness Injection` (reads "1 complimentary Wellness Injection per visit"; sent: "1 Complimentary Wellness Injection per visit") |
| Tier 1 and Tier 2 > benefit "Percent off an item" (any additional) > Per | `per visit` | `during visit` (reads "50% off any additional wellness injections during visit", exactly as sent) |

## Already as sent (check, no change expected)

- Subject `WE'RE TURNING ONE!` (curly apostrophe); Headline `WE'RE TURNING ONE! 🥂✨`.
- Greeting `Hi Beautiful🤍`, no space before the heart.
- Opening paragraphs 1 and 3; the offer summary ("Think of the Beauty Bank ..."); the note above
  the tiers, `Here's how it works, 100% of your money goes to any treatments you would like:`.
- Both tiers $299 a month (recurring); `10% off any qualifying treatments`.
- Closing paragraphs, sign-off (`With gratitude,` / `Neelam Aesthetics Team 🤍✨` / the tagline).
- Photos: header "Principals toasting", body "Principals seated"; button `Come visit 🤍` to
  https://neelamaesthetics.square.site/.
- No terms link, no disclaimer (the first send had neither).

## The birthday benefit

v1 shows "75 birthday credit during your birth month". The amount is 75 (dollars), so the stored
benefit is already right: the missing "$" is a display fault (polish item 13), not a field to re-enter.
If the Amount field holds anything other than 75, set it to 75.

## What no field can say (the typed model's limits, not re-entry items)

- The price line: sent "$299 monthly contribution (100% of your money goes to any treatments you would
  like)"; the email can only say "$299/month".
- The birthday line: sent "$75 Birthday reward during your birth month!"; a birthday credit reads
  "$75 birthday credit during your birth month".
- Sent as one line, "1 Complimentary Wellness Injection per visit, 50% off any additional wellness
  injections during visit"; the model has two benefits, two lines, and writes "complimentary" in
  lower case.
- The word "or" on its own line between the two options.

## What changes in the checks once re-entered

With the tier names as sent ("Option 1 ..." / "Option 2 ..."), the rule that two tiers share a name
(`tier-names-unique`) no longer fires: it compares whole names. The campaign is still a Must fix for
the same option twice (`tier-content-distinct`) and the same price twice (`tier-prices-increase`),
plus the missing terms link and disclaimer. Whether the name rule should see past an "Option N"
prefix is an open owner decision (WIP).
