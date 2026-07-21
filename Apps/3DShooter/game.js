/* =====================================================================
 * 霓虹边境 · 3D 第一人称射击  (Three.js r128, 无外部资源, 离线可玩)
 * 全部逻辑自包含：程序化几何体 / 射线命中 / 波次敌人 / HUD / WebAudio 音效
 * ===================================================================== */
(function () {
  "use strict";

  // ----------------------------- 全局状态 -----------------------------
  const STATE = { START: "start", PLAYING: "playing", PAUSED: "paused", OVER: "over" };
  let state = STATE.START;

  let scene, camera, renderer, clock;
  let gunMesh, muzzleLight, flashLight;
  const raycaster = new THREE.Raycaster();

  // 玩家
  const player = {
    pos: new THREE.Vector3(0, 0, 0),
    velY: 0,
    onGround: true,
    yaw: 0,
    pitch: 0,
    health: 100,
    maxHealth: 100,
    eye: 1.7,
    radius: 0.4,
  };

  // 武器
  const weapon = {
    mag: 30,
    ammo: 30,
    reserve: 999,
    fireInterval: 0.11,
    reloadTime: 1.3,
    reloading: false,
    lastShot: -10,
    recoil: 0,
  };

  // 输入
  const keys = Object.create(null);
  let pointerLocked = false;
  let firing = false;

  // 敌人 & 特效
  const enemies = [];
  let waveNumber = 0;
  let score = 0;
  let enemiesToSpawn = 0;
  let spawnTimer = 0;
  let waveCooldown = 0;
  let gameTime = 0; // 仅在 PLAYING 累加，用于受暂停影响的计时

  // 障碍盒（AABB，仅 xz 用于玩家碰撞）
  const obstacles = [];

  // 特效集合
  const tracers = [];
  const puffs = [];

  // 摄像机抖动
  let shake = 0;

  // DOM
  const $ = (id) => document.getElementById(id);
  const dom = {
    hpfill: $("hpfill"), score: $("score"), wave: $("wave"), enemies: $("enemies"),
    ammo: $("ammo"), reloadHint: $("reloadHint"), vignette: $("vignette"),
    hitmarker: $("hitmarker"), waveToast: $("waveToast"),
    startScreen: $("startScreen"), pauseScreen: $("pauseScreen"), gameOver: $("gameOver"),
    finalScore: $("finalScore"), finalWave: $("finalWave"),
    startBtn: $("startBtn"), resumeBtn: $("resumeBtn"), restartBtn: $("restartBtn"),
  };

  // ----------------------------- 音频 -----------------------------
  let audioCtx = null;
  function initAudio() {
    if (audioCtx) return;
    try { audioCtx = new (window.AudioContext || window.webkitAudioContext)(); } catch (e) { audioCtx = null; }
  }
  function beep(type, freqStart, freqEnd, dur, vol) {
    if (!audioCtx) return;
    const t = audioCtx.currentTime;
    const osc = audioCtx.createOscillator();
    const gain = audioCtx.createGain();
    osc.type = type;
    osc.frequency.setValueAtTime(freqStart, t);
    osc.frequency.exponentialRampToValueAtTime(Math.max(1, freqEnd), t + dur);
    gain.gain.setValueAtTime(vol, t);
    gain.gain.exponentialRampToValueAtTime(0.0001, t + dur);
    osc.connect(gain); gain.connect(audioCtx.destination);
    osc.start(t); osc.stop(t + dur);
  }
  function noiseBurst(dur, vol) {
    if (!audioCtx) return;
    const t = audioCtx.currentTime;
    const len = Math.floor(audioCtx.sampleRate * dur);
    const buf = audioCtx.createBuffer(1, len, audioCtx.sampleRate);
    const data = buf.getChannelData(0);
    for (let i = 0; i < len; i++) data[i] = (Math.random() * 2 - 1) * (1 - i / len);
    const src = audioCtx.createBufferSource(); src.buffer = buf;
    const gain = audioCtx.createGain(); gain.gain.value = vol;
    src.connect(gain); gain.connect(audioCtx.destination); src.start(t);
  }
  const sfx = {
    shoot: () => { beep("sawtooth", 880, 180, 0.12, 0.18); noiseBurst(0.06, 0.12); },
    hit:   () => beep("square", 320, 520, 0.06, 0.12),
    death: () => { beep("triangle", 260, 60, 0.3, 0.18); noiseBurst(0.18, 0.14); },
    reload:() => { beep("sine", 420, 620, 0.1, 0.1); setTimeout(() => beep("sine", 620, 420, 0.1, 0.1), 220); },
    hurt:  () => { beep("sawtooth", 160, 80, 0.22, 0.2); noiseBurst(0.12, 0.16); },
    empty: () => beep("square", 200, 200, 0.04, 0.08),
    wave:  () => { beep("sine", 520, 780, 0.18, 0.14); setTimeout(() => beep("sine", 780, 1040, 0.2, 0.14), 160); },
  };

  // ----------------------------- 初始化场景 -----------------------------
  function init() {
    scene = new THREE.Scene();
    scene.background = new THREE.Color(0x070b16);
    scene.fog = new THREE.Fog(0x070b16, 30, 140);

    camera = new THREE.PerspectiveCamera(78, window.innerWidth / window.innerHeight, 0.1, 1000);
    camera.rotation.order = "YXZ";

    renderer = new THREE.WebGLRenderer({ antialias: true });
    renderer.setPixelRatio(Math.min(window.devicePixelRatio, 2));
    renderer.setSize(window.innerWidth, window.innerHeight);
    renderer.shadowMap.enabled = true;
    renderer.shadowMap.type = THREE.PCFSoftShadowMap;
    $("game").appendChild(renderer.domElement);

    // 灯光
    scene.add(new THREE.HemisphereLight(0x9fc4ff, 0x10131f, 0.55));
    const amb = new THREE.AmbientLight(0x404a66, 0.6); scene.add(amb);
    const sun = new THREE.DirectionalLight(0xbfd8ff, 0.9);
    sun.position.set(40, 80, 30);
    sun.castShadow = true;
    sun.shadow.mapSize.set(1024, 1024);
    sun.shadow.camera.near = 1; sun.shadow.camera.far = 250;
    sun.shadow.camera.left = -100; sun.shadow.camera.right = 100;
    sun.shadow.camera.top = 100; sun.shadow.camera.bottom = -100;
    scene.add(sun);

    // 地面 + 科技网格
    const ground = new THREE.Mesh(
      new THREE.PlaneGeometry(220, 220),
      new THREE.MeshStandardMaterial({ color: 0x0c1424, roughness: 0.95, metalness: 0.0 })
    );
    ground.rotation.x = -Math.PI / 2;
    ground.receiveShadow = true;
    scene.add(ground);

    const grid = new THREE.GridHelper(220, 110, 0x2a6cff, 0x16314f);
    grid.position.y = 0.02;
    grid.material.opacity = 0.55; grid.material.transparent = true;
    scene.add(grid);

    buildArena();
    buildStars();
    buildGun();

    scene.add(camera);
    window.addEventListener("resize", onResize);
    bindInput();
    clock = new THREE.Clock();
    animate();
  }

  function buildArena() {
    // 边界墙（视觉）
    const wallMat = new THREE.MeshStandardMaterial({ color: 0x10203a, emissive: 0x0a1830, roughness: 0.7, metalness: 0.3 });
    const half = 105;
    const walls = [
      [0, half], [0, -half], [half, 0], [-half, 0],
    ];
    walls.forEach(([x, z]) => {
      const isX = x !== 0;
      const w = isX ? 0.5 : 210, d = isX ? 210 : 0.5;
      const wall = new THREE.Mesh(new THREE.BoxGeometry(w, 6, d), wallMat);
      wall.position.set(x, 3, z);
      wall.castShadow = true; wall.receiveShadow = true;
      scene.add(wall);
    });

    // 散布障碍箱（也用于碰撞）
    const crateMat = new THREE.MeshStandardMaterial({ color: 0x1b2c4a, emissive: 0x0c1830, roughness: 0.6, metalness: 0.35 });
    const crateMat2 = new THREE.MeshStandardMaterial({ color: 0x243a5e, emissive: 0x12233f, roughness: 0.6, metalness: 0.35 });
    const spots = [
      [12, 0, 10, 3], [-14, 0, -8, 2.4], [0, 0, -22, 4], [24, 0, -16, 2.2],
      [-26, 0, 14, 2.8], [8, 0, 28, 2.6], [-10, 0, 30, 3.2], [34, 0, 6, 2.4],
      [-34, 0, -12, 2.6], [18, 0, -34, 3], [-20, 0, -34, 2.4], [40, 0, -30, 3.4],
      [-44, 0, 2, 2.6], [4, 0, 44, 3], [-6, 0, -48, 2.8],
    ];
    spots.forEach(([x, , z, s], i) => {
      const m = new THREE.Mesh(new THREE.BoxGeometry(s, s, s), i % 2 ? crateMat : crateMat2);
      m.position.set(x, s / 2, z);
      m.castShadow = true; m.receiveShadow = true;
      scene.add(m);
      obstacles.push({ minX: x - s / 2, maxX: x + s / 2, minZ: z - s / 2, maxZ: z + s / 2 });
    });
  }

  function buildStars() {
    const g = new THREE.BufferGeometry();
    const n = 600, arr = new Float32Array(n * 3);
    for (let i = 0; i < n; i++) {
      const r = 160 + Math.random() * 60;
      const th = Math.random() * Math.PI * 2;
      const ph = Math.random() * Math.PI * 0.5;
      arr[i * 3] = Math.cos(th) * Math.cos(ph) * r;
      arr[i * 3 + 1] = 20 + Math.sin(ph) * r * 0.6;
      arr[i * 3 + 2] = Math.sin(th) * Math.cos(ph) * r;
    }
    g.setAttribute("position", new THREE.BufferAttribute(arr, 3));
    scene.add(new THREE.Points(g, new THREE.PointsMaterial({ color: 0x6fa8ff, size: 0.7, transparent: true, opacity: 0.8 })));
  }

  function buildGun() {
    gunMesh = new THREE.Group();
    const bodyMat = new THREE.MeshStandardMaterial({ color: 0x223044, roughness: 0.5, metalness: 0.7 });
    const glowMat = new THREE.MeshStandardMaterial({ color: 0x081018, emissive: 0x33e0ff, emissiveIntensity: 1.2, roughness: 0.4 });
    const body = new THREE.Mesh(new THREE.BoxGeometry(0.14, 0.16, 0.6), bodyMat);
    const barrel = new THREE.Mesh(new THREE.CylinderGeometry(0.04, 0.04, 0.5, 12), bodyMat);
    barrel.rotation.x = Math.PI / 2; barrel.position.set(0, 0.02, -0.5);
    const sight = new THREE.Mesh(new THREE.BoxGeometry(0.04, 0.05, 0.18), glowMat);
    sight.position.set(0, 0.12, -0.1);
    gunMesh.add(body); gunMesh.add(barrel); gunMesh.add(sight);
    gunMesh.position.set(0.28, -0.26, -0.5);
    camera.add(gunMesh);

    flashLight = new THREE.PointLight(0xffd27a, 0, 6, 2);
    flashLight.position.set(0.28, -0.2, -0.9);
    camera.add(flashLight);
  }

  // ----------------------------- 敌人 -----------------------------
  function spawnEnemy() {
    const angle = Math.random() * Math.PI * 2;
    const dist = 34 + Math.random() * 40;
    const x = player.pos.x + Math.cos(angle) * dist;
    const z = player.pos.z + Math.sin(angle) * dist;
    const px = Math.max(-100, Math.min(100, x));
    const pz = Math.max(-100, Math.min(100, z));

    const group = new THREE.Group();
    const bodyMat = new THREE.MeshStandardMaterial({ color: 0xff3b4e, emissive: 0x551018, emissiveIntensity: 1, roughness: 0.5, metalness: 0.25 });
    const body = new THREE.Mesh(new THREE.BoxGeometry(0.85, 1.35, 0.6), bodyMat);
    body.position.y = 0.9; body.castShadow = true;
    const headMat = new THREE.MeshStandardMaterial({ color: 0xffd24d, emissive: 0x3a2a00, emissiveIntensity: 1, roughness: 0.4 });
    const head = new THREE.Mesh(new THREE.BoxGeometry(0.55, 0.55, 0.55), headMat);
    head.position.y = 1.85; head.castShadow = true;
    const eyeMat = new THREE.MeshStandardMaterial({ color: 0x111111, emissive: 0xff2a2a, emissiveIntensity: 2 });
    const eye = new THREE.Mesh(new THREE.BoxGeometry(0.4, 0.12, 0.05), eyeMat);
    eye.position.set(0, 1.88, 0.28);

    group.add(body); group.add(head); group.add(eye);
    group.position.set(px, 0, pz);
    scene.add(group);

    const enemy = {
      group, body, bodyMat,
      health: 100, maxHealth: 100,
      speed: 2.4 + Math.random() * 1.0 + waveNumber * 0.12,
      radius: 0.7,
      flash: 0,
    };
    group.userData.enemyRef = enemy;
    body.userData.enemyRef = enemy; head.userData.enemyRef = enemy; eye.userData.enemyRef = enemy;
    enemies.push(enemy);
  }

  function killEnemy(enemy) {
    spawnPuff(enemy.group.position, 0xff5566);
    scene.remove(enemy.group);
    const i = enemies.indexOf(enemy);
    if (i >= 0) enemies.splice(i, 1);
    score += 100 + waveNumber * 10;
    sfx.death();
  }

  function damageEnemy(enemy, dmg) {
    enemy.health -= dmg;
    enemy.flash = 0.12;
    sfx.hit();
    if (enemy.health <= 0) killEnemy(enemy);
  }

  // ----------------------------- 波次 -----------------------------
  function startWave(n) {
    waveNumber = n;
    enemiesToSpawn = 4 + n * 3;
    spawnTimer = 0;
    showWaveToast("第 " + n + " 波");
    sfx.wave();
    updateHUD();
  }

  function updateWaves(dt) {
    if (enemiesToSpawn > 0) {
      spawnTimer -= dt;
      if (spawnTimer <= 0) {
        spawnEnemy();
        enemiesToSpawn--;
        spawnTimer = 0.5; // 分批生成
      }
    } else if (enemies.length === 0) {
      if (waveCooldown <= 0) {
        waveCooldown = 3.5;
        showWaveToast("波次清空！");
      }
      waveCooldown -= dt;
      if (waveCooldown <= 0) startWave(waveNumber + 1);
    }
  }

  // ----------------------------- 射击 -----------------------------
  function tryShoot() {
    if (state !== STATE.PLAYING) return;
    if (weapon.reloading) return;
    if (gameTime - weapon.lastShot < weapon.fireInterval) return;
    weapon.lastShot = gameTime;

    if (weapon.ammo <= 0) { sfx.empty(); startReload(); return; }
    weapon.ammo--;
    sfx.shoot();

    // 后坐力 & 枪口闪光
    weapon.recoil = 0.045;
    flashLight.intensity = 3.2;
    shake = Math.min(shake + 0.18, 0.5);

    // 射线
    raycaster.setFromCamera({ x: 0, y: 0 }, camera);
    const targets = enemies.map((e) => e.group);
    const hits = raycaster.intersectObjects(targets, true);

    const muzzle = new THREE.Vector3();
    flashLight.getWorldPosition(muzzle);

    let endPoint;
    if (hits.length > 0) {
      const hit = hits[0];
      endPoint = hit.point.clone();
      // 找到对应敌人
      let o = hit.object;
      while (o && !o.userData.enemyRef) o = o.parent;
      if (o && o.userData.enemyRef) {
        damageEnemy(o.userData.enemyRef, 34);
        showHitmarker();
      }
    } else {
      const dir = new THREE.Vector3();
      camera.getWorldDirection(dir);
      endPoint = muzzle.clone().add(dir.multiplyScalar(120));
    }
    spawnTracer(muzzle, endPoint);
    updateHUD();
  }

  function startReload() {
    if (weapon.reloading || weapon.ammo === weapon.mag) return;
    weapon.reloading = true;
    sfx.reload();
    weapon.reloadEnd = gameTime + weapon.reloadTime;
    dom.reloadHint.textContent = "换弹中…";
  }

  function finishReloadIfNeeded() {
    if (weapon.reloading && gameTime >= weapon.reloadEnd) {
      weapon.ammo = weapon.mag;
      weapon.reloading = false;
      dom.reloadHint.textContent = "";
    }
  }

  // ----------------------------- 特效 -----------------------------
  function spawnTracer(from, to) {
    const geo = new THREE.BufferGeometry().setFromPoints([from, to]);
    const mat = new THREE.LineBasicMaterial({ color: 0x9ff0ff, transparent: true, opacity: 0.9 });
    const line = new THREE.Line(geo, mat);
    scene.add(line);
    tracers.push({ line, life: 0.06 });
  }

  function spawnPuff(pos, color) {
    const n = 10;
    const g = new THREE.BufferGeometry();
    const arr = new Float32Array(n * 3);
    const vel = [];
    for (let i = 0; i < n; i++) {
      arr[i * 3] = pos.x; arr[i * 3 + 1] = pos.y + 1; arr[i * 3 + 2] = pos.z;
      vel.push(new THREE.Vector3((Math.random() - 0.5) * 6, Math.random() * 5 + 2, (Math.random() - 0.5) * 6));
    }
    g.setAttribute("position", new THREE.BufferAttribute(arr, 3));
    const pts = new THREE.Points(g, new THREE.PointsMaterial({ color, size: 0.35, transparent: true, opacity: 1 }));
    scene.add(pts);
    puffs.push({ pts, vel, life: 0.5 });
  }

  function updateEffects(dt) {
    for (let i = tracers.length - 1; i >= 0; i--) {
      const t = tracers[i];
      t.life -= dt;
      t.line.material.opacity = Math.max(0, t.life / 0.06) * 0.9;
      if (t.life <= 0) { scene.remove(t.line); t.line.geometry.dispose(); t.line.material.dispose(); tracers.splice(i, 1); }
    }
    for (let i = puffs.length - 1; i >= 0; i--) {
      const p = puffs[i];
      p.life -= dt;
      const pos = p.pts.geometry.attributes.position;
      for (let j = 0; j < p.vel.length; j++) {
        pos.array[j * 3] += p.vel[j].x * dt;
        pos.array[j * 3 + 1] += p.vel[j].y * dt;
        pos.array[j * 3 + 2] += p.vel[j].z * dt;
        p.vel[j].y -= 9 * dt;
      }
      pos.needsUpdate = true;
      p.pts.material.opacity = Math.max(0, p.life / 0.5);
      if (p.life <= 0) { scene.remove(p.pts); p.pts.geometry.dispose(); p.pts.material.dispose(); puffs.splice(i, 1); }
    }
  }

  let hitmarkerTimer = 0;
  function showHitmarker() {
    dom.hitmarker.style.opacity = "1";
    hitmarkerTimer = 0.12;
  }

  let waveToastTimer = 0;
  function showWaveToast(text) {
    dom.waveToast.textContent = text;
    dom.waveToast.style.opacity = "1";
    waveToastTimer = 1.6;
  }

  // ----------------------------- 玩家控制 -----------------------------
  const tmpForward = new THREE.Vector3();
  const tmpRight = new THREE.Vector3();
  const tmpMove = new THREE.Vector3();
  const up = new THREE.Vector3(0, 1, 0);

  function updatePlayer(dt) {
    camera.getWorldDirection(tmpForward);
    tmpForward.y = 0; tmpForward.normalize();
    tmpRight.crossVectors(tmpForward, up).normalize();

    tmpMove.set(0, 0, 0);
    if (keys["KeyW"]) tmpMove.add(tmpForward);
    if (keys["KeyS"]) tmpMove.sub(tmpForward);
    if (keys["KeyD"]) tmpMove.add(tmpRight);
    if (keys["KeyA"]) tmpMove.sub(tmpRight);

    let speed = keys["ShiftLeft"] || keys["ShiftRight"] ? 11 : 7;
    if (tmpMove.lengthSq() > 0) {
      tmpMove.normalize().multiplyScalar(speed * dt);
      player.pos.x += tmpMove.x;
      player.pos.z += tmpMove.z;
      resolveCollisions();
    }

    // 垂直
    player.velY -= 22 * dt;
    player.pos.y += player.velY * dt;
    if (player.pos.y <= 0) { player.pos.y = 0; player.velY = 0; player.onGround = true; }
    if ((keys["Space"]) && player.onGround) { player.velY = 8.2; player.onGround = false; }

    // 边界
    player.pos.x = Math.max(-103, Math.min(103, player.pos.x));
    player.pos.z = Math.max(-103, Math.min(103, player.pos.z));

    // 应用到相机
    camera.position.set(player.pos.x, player.pos.y + player.eye, player.pos.z);
    // 后坐力衰减
    weapon.recoil *= Math.pow(0.0001, dt);
    const shakeX = (Math.random() - 0.5) * shake;
    const shakeY = (Math.random() - 0.5) * shake;
    shake *= Math.pow(0.0005, dt);
    camera.rotation.y = player.yaw;
    camera.rotation.x = player.pitch + weapon.recoil + shakeY;
    camera.rotation.z = shakeX * 0.5;
  }

  function resolveCollisions() {
    for (let i = 0; i < obstacles.length; i++) {
      const o = obstacles[i];
      const r = player.radius;
      const cx = Math.max(o.minX, Math.min(player.pos.x, o.maxX));
      const cz = Math.max(o.minZ, Math.min(player.pos.z, o.maxZ));
      const dx = player.pos.x - cx, dz = player.pos.z - cz;
      const d2 = dx * dx + dz * dz;
      if (d2 < r * r) {
        const d = Math.sqrt(d2) || 0.0001;
        const push = (r - d);
        player.pos.x += (dx / d) * push;
        player.pos.z += (dz / d) * push;
      }
    }
  }

  // ----------------------------- 敌人逻辑 -----------------------------
  const tmpToPlayer = new THREE.Vector3();
  function updateEnemies(dt) {
    for (let i = enemies.length - 1; i >= 0; i--) {
      const e = enemies[i];
      tmpToPlayer.set(player.pos.x - e.group.position.x, 0, player.pos.z - e.group.position.z);
      const dist = tmpToPlayer.length();
      if (dist > 1.6) {
        tmpToPlayer.normalize();
        e.group.position.x += tmpToPlayer.x * e.speed * dt;
        e.group.position.z += tmpToPlayer.z * e.speed * dt;
      } else {
        // 接触伤害
        damagePlayer(26 * dt);
      }
      e.group.lookAt(player.pos.x, e.group.position.y + 1.0, player.pos.z);

      // 受击闪烁
      if (e.flash > 0) {
        e.flash -= dt;
        e.bodyMat.emissive.setHex(0xff6677);
        e.bodyMat.emissiveIntensity = 2.2;
      } else {
        e.bodyMat.emissive.setHex(0x551018);
        e.bodyMat.emissiveIntensity = 1;
      }
    }
  }

  function damagePlayer(dmg) {
    if (state !== STATE.PLAYING) return;
    player.health -= dmg;
    if (dmg > 1) { sfx.hurt(); shake = Math.min(shake + 0.25, 0.6); }
    if (player.health <= 0) {
      player.health = 0;
      gameOver();
    }
    updateHUD();
  }

  // ----------------------------- HUD -----------------------------
  function updateHUD() {
    dom.hpfill.style.width = (player.health / player.maxHealth * 100) + "%";
    dom.score.textContent = score;
    dom.wave.textContent = "第 " + waveNumber + " 波";
    dom.enemies.textContent = "剩余敌人 " + (enemies.length + enemiesToSpawn);
    dom.ammo.textContent = weapon.ammo + " / " + weapon.mag;
    dom.vignette.style.opacity = player.health < 35 ? ((35 - player.health) / 35 * 0.7).toFixed(2) : "0";
  }

  // ----------------------------- 流程 -----------------------------
  function startNewGame() {
    // 清场
    while (enemies.length) { scene.remove(enemies[0].group); enemies.shift(); }
    player.pos.set(0, 0, 0);
    player.velY = 0; player.yaw = 0; player.pitch = 0;
    player.health = player.maxHealth;
    weapon.ammo = weapon.mag; weapon.reloading = false; dom.reloadHint.textContent = "";
    score = 0; waveCooldown = 0; gameTime = 0;
    updateHUD();
    startWave(1);
  }

  function gameOver() {
    state = STATE.OVER;
    firing = false;
    exitLock();
    dom.finalScore.textContent = score;
    dom.finalWave.textContent = "抵达第 " + waveNumber + " 波";
    dom.gameOver.classList.remove("hidden");
    document.exitPointerLock();
  }

  function exitLock() { if (document.pointerLockElement) document.exitPointerLock(); }

  // ----------------------------- 输入绑定 -----------------------------
  function bindInput() {
    document.addEventListener("keydown", (e) => {
      keys[e.code] = true;
      if (e.code === "KeyR" && state === STATE.PLAYING) startReload();
      if (e.code === "Space") e.preventDefault();
    });
    document.addEventListener("keyup", (e) => { keys[e.code] = false; });

    document.addEventListener("mousemove", (e) => {
      if (!pointerLocked) return;
      const sens = 0.0022;
      player.yaw -= e.movementX * sens;
      player.pitch -= e.movementY * sens;
      player.pitch = Math.max(-1.5, Math.min(1.5, player.pitch));
    });

    document.addEventListener("mousedown", (e) => {
      if (e.button === 0 && pointerLocked && state === STATE.PLAYING) {
        firing = true;
        tryShoot();
      }
    });
    document.addEventListener("mouseup", (e) => { if (e.button === 0) firing = false; });

    document.addEventListener("pointerlockchange", () => {
      pointerLocked = document.pointerLockElement === renderer.domElement;
      if (pointerLocked) {
        if (state === STATE.START || state === STATE.OVER) {
          dom.startScreen.classList.add("hidden");
          dom.gameOver.classList.add("hidden");
          startNewGame();          // 重置血量/敌人/分数/波次
          state = STATE.PLAYING;
        } else if (state === STATE.PAUSED) {
          state = STATE.PLAYING;
          dom.pauseScreen.classList.add("hidden");
        }
      } else {
        firing = false;
        if (state === STATE.PLAYING) {
          state = STATE.PAUSED;
          dom.pauseScreen.classList.remove("hidden");
        }
      }
    });

    dom.startBtn.addEventListener("click", () => { initAudio(); renderer.domElement.requestPointerLock(); });
    dom.resumeBtn.addEventListener("click", () => { initAudio(); renderer.domElement.requestPointerLock(); });
    dom.restartBtn.addEventListener("click", () => { initAudio(); renderer.domElement.requestPointerLock(); });
  }

  function onResize() {
    camera.aspect = window.innerWidth / window.innerHeight;
    camera.updateProjectionMatrix();
    renderer.setSize(window.innerWidth, window.innerHeight);
  }

  // ----------------------------- 主循环 -----------------------------
  function animate() {
    requestAnimationFrame(animate);
    let dt = clock.getDelta();
    if (dt > 0.05) dt = 0.05;

    // 特效 & 计时器始终更新（即使暂停也轻微推进，避免卡顿感）
    updateEffects(dt);
    if (hitmarkerTimer > 0) { hitmarkerTimer -= dt; if (hitmarkerTimer <= 0) dom.hitmarker.style.opacity = "0"; }
    if (waveToastTimer > 0) { waveToastTimer -= dt; if (waveToastTimer <= 0) dom.waveToast.style.opacity = "0"; }
    flashLight.intensity *= Math.pow(0.0001, dt); // 枪口光快速衰减

    if (state === STATE.PLAYING) {
      gameTime += dt;
      if (firing) tryShoot();
      updatePlayer(dt);
      updateEnemies(dt);
      updateWaves(dt);
      finishReloadIfNeeded();
      updateHUD();
    } else if (state === STATE.START) {
      // 开始界面：缓慢自转镜头展示场景
      player.yaw += dt * 0.15;
      camera.position.set(0, player.eye, 0);
      camera.rotation.set(0, player.yaw, 0);
    }

    gunMesh.visible = (state === STATE.PLAYING);
    renderer.render(scene, camera);
  }

  // 启动
  init();
})();
