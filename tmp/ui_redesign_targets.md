# UI Redesign Targets

This summary is based on the extracted layout dump from the compiled Unity build.

## Main Scene Structure

- `UI/up`
  - top HUD strip anchored to top edge
  - contains timer, round label, and both health bars
- `UI/center`
  - middle playfield container
  - contains the avatar art, clue/image frame, and rank list
- `UI/down`
  - lower prompt area
  - contains the dialogue bubble and question count block

## High-Impact Prototype Elements

These are the biggest reasons the app still feels like a prototype:

- `UI/down/Dialogue`
  - anchored position: `[4.406, 131.965]`
  - size: `[839.907, 111.765]`
  - this is the speech-bubble style question container
- `UI/down/Question Count`
  - anchored position: `[102.0, 121.0]`
  - size: `[1035.095, 234.237]`
  - this oversized lower block likely carries the question count framing
- `UI/center/avatar`
  - anchored position: `[-661.0, -83.0]`
  - size: `[817.0, 1478.0]`
  - scale: `[0.5353, 0.5353, 0.5353]`
  - this is the giant host illustration on the left
- `UI/center/Image`
  - anchored position: `[39.332, 68.0]`
  - size: `[885.845, 562.647]`
  - scale: `[0.7421, 0.7347, 0.7421]`
  - this is the large clue image frame on the right
- `UI/center/Ranklist`
  - anchored position: `[681.3465, -9.2]`
  - size: `[304.444, 426.224]`
  - this is the right-side leaderboard card

## Top HUD Geometry

- `UI/up/ROUND`
  - anchored position: `[196.0, -50.0]`
  - size: `[340.681, 100.0]`
- `UI/up/Text (Legacy)`
  - anchored position: `[193.3, -114.3]`
  - size: `[340.681, 100.0]`
  - likely the timer text object
- `UI/up/Blood_Blue`
  - anchored position: `[-327.81, -114.9]`
  - size: `[500.0, 39.458]`
- `UI/up/Blood_Blue/value`
  - anchored position: `[-200.0, 0.888]`
  - size: `[700.0, 38.695]`
- `UI/up/Blood_Red`
  - anchored position: `[666.6596, -114.0]`
  - size: `[695.8, 37.384]`

## Rank List Row Geometry

- `UI/center/Ranklist/text`
  - title/header area
  - anchored position: `[138.0755, -31.7605]`
  - size: `[276.151, 63.521]`
- each `Leaderboard Member Prefab*`
  - row size: `[304.44, 49.15]`
- each row `Color`
  - anchored position: `[-124.8, 0.0]`
  - scale: `[0.3789, 0.3789, 0.3789]`
- each row `Name`
  - anchored position: `[5.314, 0.0]`
  - size: `[180.983, 59.508]`
- each row `Score`
  - anchored position: `[-35.064, 0.0]`
  - size: `[31.921, 59.508]`

## Safer Redesign Direction

Use these exact targets for the next pass instead of overlaying a new shell on the whole screen:

1. Replace or restyle `UI/down/Dialogue` and `UI/down/Question Count` first.
   - These are the cleanest way to remove the speech-bubble prototype look.
2. Replace the visual frame around `UI/center/Image`.
   - Keep the clue image behavior, but give it a product-grade card.
3. Retune `UI/center/Ranklist`.
   - It is already modular and can be made product-like without moving the whole layout.
4. Leave `UI/center/avatar` for later unless a better illustration asset is available.
   - It is large enough that changing it blindly can make the app worse fast.
5. Only after those are stable, retune the top HUD text and health bars.

## Files Produced

- `tmp/unity_layout_dump.json`
- `tmp/unity_ui_components.json`
- `tools/dump_unity_layout.py`
- `tools/dump_unity_ui_components.py`
