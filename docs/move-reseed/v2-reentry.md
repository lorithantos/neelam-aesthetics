# v2 re-entry: the live second-send campaign against the real second send

The live campaign `/campaigns/6d8a3c2d-...` (label "Beauty Bank -- second send") was made from the
template "Membership announcement" and entered from the old `BeautyBankEmail.SecondSend()` sample. On
2026-10-09 it was checked word for word against the second send as it went out (2026-10-01 16:38 UTC,
38 minutes after the first). Below is each field of the campaign editor that must change for v2 to
match the real email, as far as the typed model can say it. Nothing here has been changed on the live
site.

Fields are named as the campaign editor names them (block label, then field).

## Must change

| Block / field | v2 has (old sample) | Change to (as sent) |
|---|---|---|
| Subject line | `WE’RE TURNING ONE!` | `Celebrate 1 year of Neelam Aesthetics! - Correction` (the inbox subject as sent; see "Subject" below) |
| Opening, paragraph 2 | "... Your support for our small family business has meant ..." | "... Your support for our small **F**amily business has meant ..." (capital F, as sent) |
| Offer > Name | `✨The Neelam Aesthetics Beauty Bank✨` | `Introducing: ✨The Neelam Aesthetics Beauty Bank✨` (sent as two lines, "Introducing:" then the name; one line is the closest a name holds) |
| Offer > Tier 1 > Name | `Platinum Member` | `Option 1 Platinum Member` |
| Offer > Tier 2 > Name | `Platinum Member` | `Option 2 Platinum Member` |
| Tier 1 > benefit 3 "Percent off an item" > Item | `wellness injection` | `Wellness Injection` (flagged: see "The 50% Complimentary line") |
| Tier 1 > benefit 3 "Percent off an item" > Which ones | (empty) | `Complimentary` (flagged: see "The 50% Complimentary line") |
| Tier 2 > benefit 3 "Something free" > Item | `wellness injection` | `Wellness Injection` (reads "1 complimentary Wellness Injection per visit"; sent: "1 Complimentary Wellness Injection per visit") |
| Tier 2 > benefit 4 "Percent off an item" (any additional) > How often | `per visit` | `during visit` (reads "50% off any additional wellness injections during visit", exactly as sent) |

Nine fields. Tier 1's price ($149), its birthday credit (25) and its 5% are already as sent.

## The 50% Complimentary line (the email's real mistake: enter as sent, do not correct)

Sent, tier 1's last line: "50% Complimentary Wellness Injections per visit". It contradicts itself
(half off, or free?), and it is the mistake the tool exists to catch, so it must go in as sent.

- v2 has: Percent off an item, 50, Item `wellness injection`, How often `per visit`, Which ones empty.
  Reads "50% off one wellness injection per visit": a clean benefit, so the contradiction is gone and
  no check can find it.
- Closest typed form: Percent off an item, 50, Item `Wellness Injection`, How often `per visit`,
  Which ones `Complimentary`. Reads "50% off Complimentary Wellness Injections per visit": the sent
  words, capitals and plural included, plus "off". "Which ones" is meant for a qualifier such as
  "any additional"; "Complimentary" is used there as one, which is what the sent line did.
- If using "Which ones" this way is not wanted, the fallback is Item `Wellness Injection` with Which
  ones left empty ("50% off one Wellness Injection per visit"), which loses "Complimentary" and with
  it the mistake. The owner's call; the recommendation is the closest form above.

## Already as sent (check, no change expected)

- Headline `WE’RE TURNING ONE! 🥂✨`.
- Opening paragraphs 1 and 3; the offer summary ("Think of the Beauty Bank ..."); the note above
  the tiers, `Here’s how it works, 100% of your money goes to any treatments you would like:`.
- Tier 1 $149 a month (recurring), $25 birthday credit, `5% off any qualifying treatments`.
- Tier 2 $299 a month, $75 birthday credit, `10% off any qualifying treatments`, 1 free per visit.
- No terms link (the second send had none; the field stays empty).
- Closing paragraphs, sign-off (`With gratitude,` / `Neelam Aesthetics Team 🤍✨` / the tagline).
- Photos: header "Principals toasting", body "Principals seated" (the same image files as the first
  send), no text for when images do not load (the sent images had empty alt text); button
  `Come visit 🤍` to https://neelamaesthetics.square.site/.

## Fixed by the template, so no campaign field can match the email

v2 was made from "Membership announcement", whose fixed blocks are the corrected email's, not the
sends'. Two of them differ from the second send and cannot be changed from the campaign:

- Greeting: v2 sends `Hi Beautiful 🤍` (space before the heart); sent `Hi Beautiful🤍`.
- Disclaimer: v2 sends "Wellness injections are provided after consultation with a licensed provider
  and are subject to eligibility."; the second send had no disclaimer.

Matching these needs either a campaign not made from this template (as v1 apparently is) or the
template changed, which would change every campaign made from it. Not a re-entry item; a decision.

## Subject

The second send's inbox subject was `Celebrate 1 year of Neelam Aesthetics! - Correction`; the first
send's was `Celebrate 1 year of Neelam Aesthetics!`. `WE’RE TURNING ONE!` was the headline in both,
not the subject. `v1-reentry.md` lists the subject `WE'RE TURNING ONE!` as already as sent; that is
wrong, and v1's Subject line should become `Celebrate 1 year of Neelam Aesthetics!`.

## What no field can say (the typed model's limits, not re-entry items)

- The price lines: sent "$149 monthly contribution (100% of your money goes to any treatments you
  would like)" and the same for $299; the email can only say "$149/month" and "$299/month".
- The birthday lines: sent "$25 Birthday reward during your birth month!" (and $75); a birthday
  credit reads "$25 birthday credit during your birth month".
- Tier 1's last line: the "off" the closest form adds (above).
- Tier 2's last line was one line, "1 Complimentary Wellness Injection per visit, 50% off any
  additional wellness injections during visit"; the model has two benefits, two lines, and writes
  "complimentary" in lower case.
- The word "or" on its own line between the two options.

## What changes in the checks once re-entered

With the tier names as sent ("Option 1 ..." / "Option 2 ..."), `tier-names-unique` no longer fires,
as for v1. Unlike v1, the tiers now differ in price and content, so the same-option-twice and
same-price-twice rules should not fire either. Still a Must fix for the missing terms link (recurring
charge). The known-item warning on tier 1's price ("Platinum Member" is $299 in the known items) goes
once the name is "Option 1 Platinum Member", unless that name is a known tier. Whether any instant
rule sees the contradiction in "50% off Complimentary ..." is not checked here; it is the line the
AI proofread should catch.
