# Known items proposal -- from Neelam Aesthetics' sent emails

Source: the 7 `.eml` files in `C:\Users\lori_\OneDrive\Documents` (read 2026-10-09).
Only the text/plain part was used. Where that part was empty, the HTML body was
checked too. Quoted-printable is decoded. Emoji bullets (the white heart) and the
`&nbsp;` artefacts Square leaves at line ends are dropped from the quotes.
No links or images were opened.

Email keys used for citations below:

| Key | Subject | Sent (UTC / Pacific) | Kind |
|---|---|---|---|
| **BTS** | Thank you Teachers & Faculty - Back to school Promo Neelam Aesthetics | 2026-08-25 03:01 UTC / Mon 24 Aug, 8:01 pm PDT | Square Marketing campaign |
| **BOOK-1** | Book your next appointment (`...appointment0.eml`) | 2026-09-12 19:00 UTC / 12:00 pm PDT | campaign, first send |
| **BOOK-2** | Book your next appointment (`...appointment.eml`) | 2026-09-12 19:23 UTC / 12:23 pm PDT | campaign, resend 23 min later |
| **HOL** | Countdown to the Holidays - Neelam Aesthetics | 2026-09-22 23:00 UTC / 4:00 pm PDT | campaign |
| **BB-1** | Celebrate 1 year of Neelam Aesthetics! | 2026-10-01 16:00 UTC / 9:00 am PDT | campaign, Beauty Bank first send |
| **BB-2** | Celebrate 1 year of Neelam Aesthetics! - Correction | 2026-10-01 16:38 UTC / 9:38 am PDT | campaign, Beauty Bank correction |
| **CXL** | Neelam Aesthetics has canceled your appointment | 2026-06-29 15:29 UTC / 8:29 am PDT | Square Appointments transactional, not a campaign |

---

## 1. Offers per email

### BTS -- Back to school promo (2026-08-24 PDT)
- Text part holds only the heading "BACK TO SCHOOL PROMOTION" and the button "Book Your Appointment".
- The offer itself is **in an image**. The image has empty alt text (`alt=""`) and is wrapped in a link. Nothing in the text or HTML states it.
- **Offer not recoverable from the email text.** The subject says it is for teachers and faculty. The discount, the services and the end date are unknown without the image, which was not opened.

### BOOK-1 / BOOK-2 -- Book your next appointment (2026-09-12)
- **No offer.** This is a scheduling notice: Neelam's NP clinical rotations limit availability, so book ahead for the holidays.
- Names treatments: "If you're thinking about Botox, filler, or any another treatment, we encourage you to schedule ahead."
- How the two sends differ:
  - BOOK-1: "clinical rotations throughout October" and "our availability in October will be limited."
  - BOOK-2: "clinical rotations Starting October through December" and "our availability started October will be limited."
  - BOOK-2 still says "spending much of the month outside of the office", which no longer fits a three-month span.
- Sign-off on both: "Priya & Neelam".

### HOL -- Countdown to the Holidays (2026-09-22)
- **No offer.** A reminder that availability is limited "from October through December 18", so reserve holiday appointments early.
- No treatment names. Signed "The Neelam Aesthetics Team", "Snohomish, WA | 425-877-8646".

### BB-1 -- Celebrate 1 year (first send, 2026-10-01 9:00 am PDT)
Program: "The Neelam Aesthetics Beauty Bank", introduced as "your personal beauty savings account".
Membership, two options, **identical** (the mistake):

> Option 1 Platinum Member:
> $299 monthly contribution (100% of your money goes to any treatments you would like)
> $75 Birthday reward during your birth month!
> 10% off any qualifying treatments
> 1 Complimentary Wellness Injection per visit, 50% off any additional wellness injections during visit
>
> or
>
> Option 2 Platinum Member:
> *(the same four lines, word for word)*

No end date, no other promotion. Button: "Come visit".

### BB-2 -- Correction (2026-10-01 9:38 am PDT, 38 minutes later)
- Body identical to BB-1 except **Option 1**. The HTML part matches the text part.
- The body does not say what was corrected.

> Option 1 Platinum Member:
> $149 monthly contribution (100% of your money goes to any treatments you would like)
> $25 Birthday reward during your birth month!
> 5% off any qualifying treatments
> 50% Complimentary Wellness Injections per visit
>
> or
>
> Option 2 Platinum Member:
> $299 monthly contribution (100% of your money goes to any treatments you would like)
> $75 Birthday reward during your birth month!
> 10% off any qualifying treatments
> 1 Complimentary Wellness Injection per visit, 50% off any additional wellness injections during visit

Both options are **still named "Platinum Member"** after the correction.

### CXL -- Appointment canceled (2026-06-29, transactional)
- **No offer.** It names the booked service "Cheek Filler".
- It states the cancellation policy ($100 fee within 48 hours) and the phone number "(425) 773-5261".

---

## 2. Proposed known items

### 2a. Treatments / services (3)

| Proposed item | As written, with sources | Notes |
|---|---|---|
| **Wellness Injection** | "Wellness Injection" (BB-1 x2, BB-2 x1); "Wellness Injections" (BB-2 x1); "wellness injections" (BB-1 x2, BB-2 x1) | Capitalised form is the most common (4 of 7), and the singular is the base form. **Varies** in case and plural, sometimes within one line. |
| **Botox** | "Botox" (BOOK-1, BOOK-2) | Always capitalised. |
| **Filler** | "filler" (BOOK-1, BOOK-2), always lowercase mid-sentence | Capitalising it for the list is my choice, not hers; the owner may prefer "filler". |

Candidate, not proposed as written:
- **Cheek Filler** (CXL only). This is a Square Appointments service name, not her marketing wording. List it only if the owner wants booking-system service names; it would also need a rule about how it relates to "filler".

Not treatments, so not proposed: "treatments" (generic), "Nurse Practitioner program" / "nurse practitioner clinicals" (BOOK / HOL; capitalisation **varies**).

### 2b. Benefit lines

Each line has a fixed wording with an amount that changes between tiers. If the known-items page can hold a line with a variable amount, propose the 3 patterns plus 2 fixed lines. If not, propose the 7 literal lines.

| # | Pattern | Literal lines seen, with sources |
|---|---|---|
| B1 | "$N monthly contribution (100% of your money goes to any treatments you would like)" | "$299 ..." (BB-1 x2, BB-2 x1); "$149 ..." (BB-2 x1) |
| B2 | "$N Birthday reward during your birth month!" | "$75 ..." (BB-1 x2, BB-2 x1); "$25 ..." (BB-2 x1) |
| B3 | "N% off any qualifying treatments" | "10% ..." (BB-1 x2, BB-2 x1); "5% ..." (BB-2 x1) |
| B4 | "1 Complimentary Wellness Injection per visit, 50% off any additional wellness injections during visit" | BB-1 x2, BB-2 x1. Fixed wording. |
| B5 | "50% Complimentary Wellness Injections per visit" | BB-2 x1 only. **Flag:** see section 3. |

Counts: 5 lines as patterns (B1-B3 patterns, plus B4 and B5), or 7 as literals ($299, $149, $75, $25, 10%, 5% and B4). B5 is held back until the owner confirms its wording.

Related sentence, not a benefit line: "Here's how it works, 100% of your money goes to any treatments you would like:" (BB-1, BB-2). It repeats B1's parenthesis.

### 2c. Tiers (program: "The Neelam Aesthetics Beauty Bank")

The labels "Option 1" and "Option 2" are list positions, not tier names. In BB-2, Option 1 is the cheaper tier.

| Tier | Monthly | Benefit lines | Sources | Confidence |
|---|---|---|---|---|
| **Platinum Member** | $299 | B1 $299; B2 $75; B3 10%; B4 | BB-1 (both options), BB-2 (Option 2) | **Solid.** Sent three times with the same wording, and kept unchanged by the correction. |
| **(name not given)**, sent as "Option 1 Platinum Member" | $149 | B1 $149; B2 $25; B3 5%; B5 "50% Complimentary Wellness Injections per visit" | BB-2 only | **Price and the first 3 lines are as sent. The name and the 4th line are open.** |

Guesses in the $149 tier:
- **Name:** The emails never name it anything but "Platinum Member", which is the $299 tier's name. Any other name (Gold, Silver, Essential, ...) would be a **guess** that only the owner can supply. Until she does, the $149 tier is known only by price.
- **Wellness benefit:** "50% Complimentary Wellness Injections per visit" contradicts itself (complimentary means free). The intended meaning is probably "50% off Wellness Injections per visit", but **that is a guess**. Do not store the corrected wording as known until she confirms it. Storing her wording as sent would make the checker accept it.
- **Single source:** The tier comes from one email, the correction, so nothing sent later confirms it.

---

## 3. Flags

### Wording that varies between emails
- **Wellness injection(s):** capitalisation and plural vary: "Wellness Injection", "Wellness Injections", "wellness injections".
- **Beauty Bank tier name:** "Platinum Member" is used for both price points in BB-2.
- **Clinical schedule:** BOOK-1 says "throughout October"; BOOK-2 says "Starting October through December"; HOL says "from October through December 18".
- **Phone number (not a known-item kind, but an owner check):** HOL "425-877-8646" vs CXL "(425) 773-5261".
- **Greeting:** "Hi Beautiful" (BB, BOOK) vs "Hi beautiful!" (HOL). Not an item.
- **Nurse Practitioner:** capitalised in BOOK, lowercase in HOL. Not an item.

### Source mistakes that should NOT become known items
- "any another treatment" (BOOK-1, BOOK-2). It should be "any other".
- "our availability started October will be limited" (BOOK-2). This is broken grammar introduced by the resend.
- "Starting October" (BOOK-2): capital S mid-sentence.
- "spending much of the month" (BOOK-2): left over from BOOK-1's single month.
- "50% Complimentary Wellness Injections per visit" (BB-2): contradicts itself, as discussed above.
- Two tiers both named "Platinum Member" (BB-1 at the same price; BB-2 at different prices). The BB-2 correction changed the price and benefits but not the name.
- "our small Family business" (BB-1, BB-2): stray capital F. Minor, not an item.
- "Priya &amp; Neelam" appears literally in the BOOK text parts. This is a Square text-part artefact (the HTML shows "&"), not her typo.

### Evidence gaps
- **BTS offer is image-only.** It is the one real promotion in the set besides the Beauty Bank, and its terms cannot be read from the email. Ask the owner for the image or its wording before adding promotion items.
- There are no gift cards, bundles, events, dated discounts or specials anywhere in the readable text.
