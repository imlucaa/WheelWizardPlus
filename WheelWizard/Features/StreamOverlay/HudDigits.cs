using WheelWizard.LoungeStats;

namespace WheelWizard.StreamOverlay;

/// <summary>
/// Wheel Wizard's own additions to the VR overlay page: the "Race HUD digits" number style (the VR number drawn with
/// the Mario Kart Wii race HUD digits from the user's hud-font folder, see <see cref="HudArt.FontFolder"/>), the VR
/// rank badge (Retro Rewind's E to Crown badges from the user's vr-ranks folder), and the card-less look.
/// </summary>
public static class HudDigits
{
    public const string ScriptPath = "/rewind-overlay/hud-digits.js";
    public const string GlyphPath = "/overlay/glyph/";
    public const string RankPath = "/overlay/rank/";

    /// <summary>The folder with the rank badge pictures (e.webp … crown.webp), the user's own files.</summary>
    public static string? RankFolder { get; set; }

    /// <summary>Retro Rewind's VR rank badges, highest first: the badge a VR earns is the first whose minimum it reaches.</summary>
    public static readonly (int MinVr, string Name, string Label)[] Ranks =
    [
        (100_000, "crown", "Crown"),
        (94_000, "3star", "3 stars"),
        (84_000, "2star", "2 stars"),
        (72_000, "star", "1 star"),
        (60_000, "a", "A"),
        (48_000, "b", "B"),
        (36_000, "c", "C"),
        (24_000, "d", "D"),
        (0, "e", "E"),
    ];

    /// <summary>The rank badge's name for a VR ("e" below 24,000 up to "crown" from 100,000).</summary>
    public static string RankFor(int vr) => Ranks.First(rank => vr >= rank.MinVr).Name;

    /// <summary>The picture of a rank badge, or null when it is not in the vr-ranks folder.</summary>
    public static string? RankFile(string name) =>
        RankFolder is { } folder
        && Ranks.Any(rank => rank.Name == name)
        && System.IO.Path.Combine(folder, name + ".webp") is var file
        && System.IO.File.Exists(file)
            ? file
            : null;

    /// <summary>The digits' look for a VR number colour: that colour on top, a little darker at the bottom.</summary>
    public static HudLook Look(string vrColor)
    {
        var top = LoungeStatsConfig.IsHex(vrColor) ? LoungeStatsConfig.Normalize(vrColor) : "#F2F20A";
        return new HudLook(top, HudArt.Mix(top, "#000000", 0.25), "#0A0A0A", "#77751C", 4, "#E5173F", "#2B0408", "#000000", "#FF7896");
    }

    /// <summary>No card: the badge's background, border and glow are hidden, leaving the text and numbers.</summary>
    public static string NoCardCss =>
        ".overlay-shell,.overlay-card{background:none!important;border:0!important;box-shadow:none!important;padding:0!important;"
        + "animation:none!important}.overlay-shell::before,.overlay-shell::after,.overlay-card::before,.overlay-card::after"
        + "{display:none!important}.background-layer,.background-fallback,.background-shade{display:none!important}";

    /// <summary>The page's VR number is hidden and these digits are drawn over it, in the same place and size.</summary>
    public static string Css =>
        ".vr-value{color:transparent!important;text-shadow:none!important;-webkit-text-stroke:0!important}"
        // The race HUD look is cleaner without the VR label, and the change has no pill around it.
        + ".element-vr-label{display:none!important}.delta{background:none!important;border:0!important;box-shadow:none!important}"
        + ".delta-arrow{display:none!important}"
        // The rank and the context lines lose their chips too; the script writes them in the game font.
        + ".rank-chip,.connection,.context-line>span{background:none!important;border:0!important;box-shadow:none!important}"
        + ".connection>i{display:none!important}";

    /// <summary>What the game font can write: arrows become + and -, letters go upper case, the rest is left out.</summary>
    public static string Clean(string text) =>
        System
            .Text.RegularExpressions.Regex.Replace(
                System.Text.RegularExpressions.Regex.Replace(
                    new string(
                        text.Replace('\u25b2', '+')
                            .Replace('\u25bc', '-')
                            .ToUpperInvariant()
                            .Where(c => char.IsAsciiDigit(c) || char.IsAsciiLetterUpper(c) || c is '+' or '-' or ' ')
                            .ToArray()
                    ),
                    @"\s+",
                    " "
                ),
                @"([+-]) (?=[0-9])",
                "$1"
            )
            .Trim();

    /// <summary>
    /// Draws on a layer of its own (outside the page's React tree, so the page never fights it) over every VR number,
    /// following its position, size and the count animation: the race HUD digits (d=1) and the rank badge left of the
    /// number (r=1). The digits' colour travels in the script's link (c).
    /// </summary>
    public const string Script = """
        (() => {
          const q = new URL(document.currentScript.src).searchParams;
          const color = q.get('c') || '';
          const digits = q.get('d') === '1';
          // The race HUD look: the name and the VR change in the game font too.
          const textFont = q.get('t') === '1';
          // The race HUD VR change: after a race the +N counts down while the number counts up, then it goes.
          const raceCount = q.get('hc') === '1';
          let lastDelta = null;
          let vrStart = 0;
          const rank = q.get('r') === '1';
          // The rank badge: size and gap in percent of the number's height, and its outline.
          const badgeSize = Math.min(300, Math.max(30, parseFloat(q.get('bs')) || 100)) / 100;
          const badgeGap = Math.min(100, Math.max(0, parseFloat(q.get('bg') ?? '15'))) / 100;
          const ro = q.get('ro');
          const rw = Math.min(10, Math.max(1, parseFloat(q.get('rw')) || 3));
          const outline = h => ro ? [[1,0],[-1,0],[0,1],[0,-1],[.7,.7],[-.7,.7],[.7,-.7],[-.7,-.7]].map(([x, y]) => `drop-shadow(${x * h}px ${y * h}px 0 #${ro})`).join(' ') : 'none';
          // MMR: the rank badge is the RR Lounge division's (the division travels in the link, so a new one reloads).
          const lounge = q.get('m') === '1' ? '/overlay/rank/lounge?d=' + encodeURIComponent(q.get('div') || '') : null;
          const ranks = [[100000, 'crown'], [94000, '3star'], [84000, '2star'], [72000, 'star'], [60000, 'a'], [48000, 'b'], [36000, 'c'], [24000, 'd'], [0, 'e']];
          const layer = document.createElement('div');
          layer.style.cssText = 'position:fixed;left:0;top:0;width:0;height:0;pointer-events:none;z-index:2147483647';
          document.body.appendChild(layer);
          const name = c => c === '-' ? 'dash' : c === '+' ? 'plus' : c;
          // The name in the text colour, the change in the gain or loss colour, the number in the VR number colour.
          const tint = { name: q.get('tc') || color, gain: q.get('gc') || color, loss: q.get('lc') || color, rank: q.get('rc') || color, muted: q.get('mc') || color };
          // What the game font can write: arrows become + and -, letters go upper case, the rest is left out.
          const clean = t => t.replace(/\u25b2/g, '+').replace(/\u25bc/g, '-').toUpperCase().replace(/[^0-9A-Z+\- ]/g, '').replace(/\s+/g, ' ').replace(/([+-]) (?=[0-9])/g, '$1').trim();
          const glyph = (c, col) => '/overlay/glyph/' + name(c) + '?c=' + encodeURIComponent(col || color);
          const drawable = text => /^[0-9A-Z+\- ]+$/.test(text);
          // What is drawn in the game font: the VR number always, the name and the change only in the race HUD look.
          function targets() {
            const list = [...document.querySelectorAll('.vr-value')].map(el => ({ el, kind: 'vr', text: (el.textContent || '').replace(/[^0-9-]/g, '') }));
            if (textFont) {
              for (const el of document.querySelectorAll('.player-name'))
                list.push({ el, kind: 'name', text: (el.textContent || '').trim().toUpperCase() });
              for (const el of document.querySelectorAll('.rank-chip > span'))
                list.push({ el, kind: 'extra', col: tint.rank, text: clean(el.textContent || '') });
              for (const el of document.querySelectorAll('.rank-chip > b'))
                list.push({ el, kind: 'extra', col: el.classList.contains('negative') ? tint.loss : tint.gain, text: clean(el.textContent || '') });
              for (const el of document.querySelectorAll('.connection, .context-line > span'))
                list.push({
                  el,
                  kind: 'extra',
                  col: el.classList.contains('positive') ? tint.gain : el.classList.contains('negative') ? tint.loss : tint.muted,
                  text: clean(el.textContent || ''),
                });
              const vrNow = parseInt((document.querySelector('.vr-value')?.textContent || '').replace(/[^0-9-]/g, ''), 10) || 0;
              for (const el of document.querySelectorAll('.element-delta .delta')) {
                let text = (el.textContent || '').replace(/[^0-9+-]/g, '');
                if (raceCount) {
                  el.style.color = 'transparent';
                  const delta = parseInt(text, 10) || 0;
                  // The first change seen is an old one (already counted); a new one starts counting from here.
                  if (lastDelta === null) vrStart = vrNow - delta;
                  else if (text !== lastDelta) vrStart = vrNow;
                  lastDelta = text;
                  const left = delta - (vrNow - vrStart);
                  // More left than the change itself: the number jumped (the page loading), so it counts as done.
                  if (Math.abs(left) > Math.abs(delta)) {
                    vrStart = vrNow - delta;
                    continue;
                  }
                  if (left === 0 || Math.sign(left) !== Math.sign(delta)) continue;
                  text = (left > 0 ? '+' : '') + left;
                }
                list.push({ el, kind: 'delta', text });
              }
            }
            return list.filter(t => t.text && drawable(t.text));
          }
          let last = '';
          function frame() {
            const items = targets().map(t => {
              const r = t.el.getBoundingClientRect();
              const cs = getComputedStyle(t.el);
              return { ...t, r, size: parseFloat(cs.fontSize) || r.height, pad: parseFloat(cs.paddingLeft) || 0 };
            }).filter(i => i.r.width > 0 && (i.kind !== 'vr' || digits || rank));
            const key = items.map(i => i.kind + i.text + '|' + Math.round(i.r.left) + '|' + Math.round(i.r.right) + '|' + Math.round(i.r.top) + '|' + Math.round(i.size)).join(';');
            if (key !== last) {
              last = key;
              layer.textContent = '';
              for (const i of items) {
                const h = i.size * 1.08;
                const fontHere = i.kind === 'vr' ? digits : true;
                if (fontHere)
                  i.el.style.color = 'transparent';
                const box = document.createElement('div');
                // The name keeps its left edge; numbers keep their right edge. Without the digits the rank badge sits
                // just left of the page's own number.
                const side = i.kind === 'name' || i.kind === 'extra' ? `left:${i.r.left + i.pad}px` : `right:${fontHere ? innerWidth - i.r.right : innerWidth - i.r.left + h * badgeGap}px`;
                box.style.cssText = `position:fixed;top:${i.r.top + (i.r.height - h) / 2}px;${side};height:${h}px;display:flex;align-items:center`;
                if (rank && i.kind === 'vr') {
                  const vr = parseInt(i.text, 10) || 0;
                  const badge = document.createElement('img');
                  badge.src = lounge || '/overlay/rank/' + ranks.find(t => vr >= t[0])[1];
                  badge.style.cssText = `height:${h * 0.9 * badgeSize}px;width:auto;margin-right:${fontHere ? h * badgeGap : 0}px;filter:${outline(Math.max(1, h * 0.015 * rw))}`;
                  box.appendChild(badge);
                }
                if (fontHere)
                  for (const c of i.text) {
                    if (c === ' ') {
                      const gap = document.createElement('span');
                      gap.style.cssText = `display:inline-block;width:${h * 0.3}px`;
                      box.appendChild(gap);
                      continue;
                    }
                    const img = document.createElement('img');
                    img.src = glyph(c, i.col ? i.col : i.kind === 'name' ? tint.name : i.kind === 'delta' ? (i.text.startsWith('-') ? tint.loss : tint.gain) : color);
                    img.style.cssText = `height:${h}px;width:auto;margin-left:${-h * 0.025}px`;
                    box.appendChild(img);
                  }
                layer.appendChild(box);
              }
            }
            requestAnimationFrame(frame);
          }
          requestAnimationFrame(frame);
        })();
        """;
}
