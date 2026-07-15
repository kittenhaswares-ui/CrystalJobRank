# Third-party notices

## Final Fantasy XIV job icons

Job icon textures are owned by Square Enix. Crystal Job Rank does not copy or
redistribute them; the plugin requests the icons at runtime from the user's
installed game files through Dalamud's texture service. The rank frames and
job-themed ornament drawing code in this repository are original to this
project.

The non-shipping art-direction board at
`assets/concepts/job-rank-upgrades.png` was generated with OpenAI image
generation. It is a visual design reference, not a source of the runtime job
icons.

## PvP Tracker interoperability research

The Crystalline Conflict result-packet layout and the current match-end hook
signature were researched and cross-checked against:

- PvP Tracker / PvpStats by SaMo (`wrath16/PvpStats`)
- Source: https://github.com/wrath16/PvpStats
- License: MIT

No PvP Tracker UI, database, timeline, action parser, or live-combat code is
included. The packet declaration in this repository is a small, independently
structured interoperability boundary containing offsets required to read the
game's post-match payload.

The upstream repository publishes the following license notice verbatim:

> MIT License
>
> Copyright (c) [year] [fullname]
>
> Permission is hereby granted, free of charge, to any person obtaining a copy
> of this software and associated documentation files (the "Software"), to deal
> in the Software without restriction, including without limitation the rights
> to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
> copies of the Software, and to permit persons to whom the Software is
> furnished to do so, subject to the following conditions:
>
> The above copyright notice and this permission notice shall be included in all
> copies or substantial portions of the Software.
>
> THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
> IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
> FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
> AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
> LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
> OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
> SOFTWARE.
