(function(){
  const container = document.querySelector('.pi-svg-container');
  if (!container || !container.shadowRoot) return JSON.stringify({err:'no shadow'});
  const svg = container.shadowRoot.querySelector('svg');

  // Collect text elements
  const texts = Array.from(svg.querySelectorAll('text'));
  const items = texts.map(t => {
    const r = t.getBoundingClientRect();
    return {x: Math.round(r.left + r.width/2), y: Math.round(r.top + r.height/2), txt: t.textContent.trim()};
  }).filter(i => i.txt);

  // Collect images
  const imgs = Array.from(svg.querySelectorAll('image'));
  const imgList = imgs.map(i => {
    const r = i.getBoundingClientRect();
    return {
      x: Math.round(r.left + r.width/2),
      y: Math.round(r.top + r.height/2),
      w: Math.round(r.width),
      h: Math.round(r.height),
      href: (i.getAttribute('href') || i.getAttribute('xlink:href') || '').split('/').pop()
    };
  });

  // Switch toggle images (w 40-45, h 18)
  const switchImgs = imgList.filter(i => i.w >= 38 && i.w <= 46 && i.h >= 17 && i.h <= 20);
  // Known ON/OFF hrefs
  const ON_HREFS = new Set();
  const OFF_HREFS = new Set();
  // We don't know yet - use first as reference and check by hovering? Just record both.

  // For each card name, find the corresponding data
  const byY = {};
  for (const it of items) {
    if (!byY[it.y]) byY[it.y] = [];
    byY[it.y].push(it);
  }
  for (const y in byY) byY[y].sort((a,b) => a.x - b.x);

  // Find name rows: row with N>=8 items matching /^[A-Z0-9\-]+$/i, not all matching /^\d+F$/
  const nameRows = [];
  for (const yStr in byY) {
    const arr = byY[yStr];
    if (arr.length >= 8 && arr.every(it => /^[A-Z0-9\-]+$/i.test(it.txt) && it.txt.length < 20)) {
      if (arr.every(it => /^[A-Z]?\d+F$/i.test(it.txt))) continue;
      nameRows.push({y: parseInt(yStr), items: arr});
    }
  }

  function nearest(arr, x, y, xMax, yMax) {
    let best = null, bestDist = 9999;
    for (const it of arr) {
      const dx = Math.abs(it.x - x);
      const dy = Math.abs(it.y - y);
      if (dx > xMax || dy > yMax) continue;
      const d = dx*dx + dy*dy;
      if (d < bestDist) { bestDist = d; best = it; }
    }
    return best;
  }

  // Group switch imgs by href
  const switchByHref = {};
  for (const si of switchImgs) {
    if (!si.href) continue;
    if (!switchByHref[si.href]) switchByHref[si.href] = 0;
    switchByHref[si.href]++;
  }
  // The href with majority count is OFF, the minority is ON.
  // If only one href is visible, leave the state unknown instead of assuming OFF.
  const hrefs = Object.keys(switchByHref);
  let offHref = null, onHref = null;
  if (hrefs.length > 1) {
    hrefs.sort((a, b) => switchByHref[b] - switchByHref[a]);
    offHref = hrefs[0];
    onHref = hrefs[1];
  }

  // Mode texts: 制冷/通风/制热/送暖
  const modeTexts = items.filter(i => /^(制冷|通风|制热|送暖)$/.test(i.txt));

  const cards = [];
  for (const row of nameRows) {
    for (const nameIt of row.items) {
      const x = nameIt.x;
      const y = nameIt.y;
      // switch image at y+~100
      const sw = nearest(switchImgs, x, y + 100, 80, 50);
      let swState = '-';
      if (sw) {
        if (sw.href === onHref) swState = 'ON';
        else if (sw.href === offHref) swState = 'OFF';
        else swState = '?'; // unknown
      }
      // mode text (if any)
      const mode = nearest(modeTexts, x, y + 140, 80, 30);
      // indoor temp
      const indoor = nearest(items.filter(i => /\d+(\.\d+)?\s*\u2103/.test(i.txt)), x, y + 170, 80, 40);
      // set temp
      const setT = nearest(items.filter(i => /^\d+(\.\d+)?\s*\u2103$/.test(i.txt)), x, y + 200, 80, 40);
      // fan
      const fan = nearest(items.filter(i => /^(自动|高|中|低|0|1|2|3)$/.test(i.txt)), x, y + 235, 80, 40);
      cards.push({
        name: nameIt.txt,
        switch: swState,
        mode: mode ? mode.txt : 'Cool',
        indoor: indoor ? indoor.txt.replace(/\s*\u2103/, '') : '-',
        setTemp: setT ? setT.txt.replace(/\s*\u2103/, '') : '-',
        fan: fan ? fan.txt : '-'
      });
    }
  }
  return JSON.stringify({
    switchOnHref: onHref,
    switchOffHref: offHref,
    count: cards.length,
    cards: cards
  });
})()
