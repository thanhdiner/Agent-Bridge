(() => {
  const reduceMotion = window.matchMedia('(prefers-reduced-motion: reduce)').matches;
  const coarsePointer = window.matchMedia('(pointer: coarse)').matches;
  const header = document.querySelector('.site-header');
  const menuButton = document.querySelector('.menu-button');
  const nav = document.querySelector('.nav-links');

  const progress = document.createElement('div');
  progress.className = 'scroll-progress';
  progress.setAttribute('aria-hidden', 'true');
  document.body.prepend(progress);

  const ambient = document.createElement('div');
  ambient.className = 'ambient-layer';
  ambient.setAttribute('aria-hidden', 'true');
  ambient.innerHTML = '<span class="ambient-orb one"></span><span class="ambient-orb two"></span><span class="ambient-orb three"></span>';
  document.body.prepend(ambient);

  requestAnimationFrame(() => document.body.classList.add('ready'));

  let scrollTicking = false;
  let scrollIdleTimer = 0;
  let lastScrollY = window.scrollY;
  let lastScrollAt = Date.now();
  let revealNodes = [];

  const updateScrollUI = () => {
    const scrollable = Math.max(document.documentElement.scrollHeight - window.innerHeight, 1);
    const amount = Math.min(Math.max(window.scrollY / scrollable, 0), 1);
    document.documentElement.style.setProperty('--scroll-progress', amount.toFixed(4));
    header?.classList.toggle('scrolled', window.scrollY > 12);
    scrollTicking = false;
  };

  const requestScrollUpdate = () => {
    const now = Date.now();
    const currentY = window.scrollY;
    const delta = Math.abs(currentY - lastScrollY);
    const elapsed = Math.max(now - lastScrollAt, 1);
    const velocity = delta / elapsed;

    document.body.classList.add('is-scrolling');
    window.clearTimeout(scrollIdleTimer);
    scrollIdleTimer = window.setTimeout(() => {
      document.body.classList.remove('is-scrolling', 'is-fast-scrolling');
    }, 350);

    if (delta > window.innerHeight * 0.55 || velocity > 3.5) {
      document.body.classList.add('is-fast-scrolling');
      revealNodes.forEach((node) => node.classList.add('visible'));
    }

    lastScrollY = currentY;
    lastScrollAt = now;

    if (scrollTicking) return;
    scrollTicking = true;
    requestAnimationFrame(updateScrollUI);
  };

  updateScrollUI();
  window.addEventListener('scroll', requestScrollUpdate, { passive: true });
  window.addEventListener('resize', requestScrollUpdate, { passive: true });

  menuButton?.addEventListener('click', () => {
    const open = menuButton.getAttribute('aria-expanded') === 'true';
    menuButton.setAttribute('aria-expanded', String(!open));
    nav?.classList.toggle('open', !open);
  });

  nav?.querySelectorAll('a').forEach((link) => {
    link.addEventListener('click', () => {
      nav.classList.remove('open');
      menuButton?.setAttribute('aria-expanded', 'false');
    });
  });

  revealNodes = [...document.querySelectorAll('.reveal')];
  revealNodes.forEach((node, index) => {
    node.style.setProperty('--reveal-delay', `${Math.min((index % 4) * 65, 195)}ms`);
  });

  if (reduceMotion) {
    revealNodes.forEach((node) => node.classList.add('visible'));
  } else {
    const revealObserver = new IntersectionObserver(
      (entries, observer) => {
        entries.forEach((entry) => {
          if (!entry.isIntersecting) return;
          entry.target.classList.add('visible');
          observer.unobserve(entry.target);
        });
      },
      { threshold: 0.12, rootMargin: '0px 0px -6% 0px' }
    );
    revealNodes.forEach((node) => revealObserver.observe(node));
  }

  document.querySelectorAll('.faq-item button').forEach((button) => {
    button.addEventListener('click', () => {
      const item = button.closest('.faq-item');
      const willOpen = !item.classList.contains('open');

      document.querySelectorAll('.faq-item').forEach((other) => {
        other.classList.remove('open');
        other.querySelector('button')?.setAttribute('aria-expanded', 'false');
      });

      if (willOpen) {
        item.classList.add('open');
        button.setAttribute('aria-expanded', 'true');
      }
    });
  });

  const demoButtons = [...document.querySelectorAll('[data-demo-step]')];
  const demoScreens = [...document.querySelectorAll('[data-demo-screen]')];
  let currentDemo = 0;
  let demoTimer;

  const showDemo = (index) => {
    currentDemo = index;
    demoButtons.forEach((button, i) => button.classList.toggle('active', i === index));
    demoScreens.forEach((screen, i) => screen.classList.toggle('active', i === index));
  };

  const restartDemo = () => {
    window.clearInterval(demoTimer);
    if (reduceMotion || document.hidden || demoButtons.length < 2) return;
    demoTimer = window.setInterval(() => showDemo((currentDemo + 1) % demoButtons.length), 4800);
  };

  demoButtons.forEach((button, index) => {
    button.addEventListener('click', () => {
      showDemo(index);
      restartDemo();
    });
  });

  document.addEventListener('visibilitychange', restartDemo);
  restartDemo();

  const motionSurfaces = document.querySelectorAll(
    '.card, .machine, .phone, .demo-stage, .pricing-plan, .policy-card, .stat-card'
  );

  motionSurfaces.forEach((surface) => {
    surface.classList.add('motion-surface');
    if (reduceMotion || coarsePointer) return;

    surface.addEventListener('pointermove', (event) => {
      const bounds = surface.getBoundingClientRect();
      surface.style.setProperty('--mx', `${event.clientX - bounds.left}px`);
      surface.style.setProperty('--my', `${event.clientY - bounds.top}px`);
    });
  });

  if (!reduceMotion && !coarsePointer) {
    let pointerFrame = 0;
    window.addEventListener(
      'pointermove',
      (event) => {
        if (pointerFrame) return;
        pointerFrame = requestAnimationFrame(() => {
          document.body.style.setProperty('--pointer-x', `${event.clientX}px`);
          document.body.style.setProperty('--pointer-y', `${event.clientY}px`);
          pointerFrame = 0;
        });
      },
      { passive: true }
    );

    const heroVisual = document.querySelector('.hero-visual');
    const machine = document.querySelector('.machine');
    const phone = document.querySelector('.phone');

    heroVisual?.addEventListener('pointermove', (event) => {
      const bounds = heroVisual.getBoundingClientRect();
      const x = (event.clientX - bounds.left) / bounds.width - 0.5;
      const y = (event.clientY - bounds.top) / bounds.height - 0.5;

      machine?.style.setProperty('--tilt-x', `${(-y * 3.5).toFixed(2)}deg`);
      machine?.style.setProperty('--tilt-y', `${(x * 5).toFixed(2)}deg`);
      phone?.style.setProperty('--tilt-x', `${(-y * 2.5).toFixed(2)}deg`);
      phone?.style.setProperty('--tilt-y', `${(x * 4).toFixed(2)}deg`);
    });

    heroVisual?.addEventListener('pointerleave', () => {
      machine?.style.setProperty('--tilt-x', '0deg');
      machine?.style.setProperty('--tilt-y', '0deg');
      phone?.style.setProperty('--tilt-x', '0deg');
      phone?.style.setProperty('--tilt-y', '0deg');
    });

    document.querySelectorAll('.button').forEach((button) => {
      button.addEventListener('pointermove', (event) => {
        const bounds = button.getBoundingClientRect();
        const x = event.clientX - bounds.left - bounds.width / 2;
        const y = event.clientY - bounds.top - bounds.height / 2;
        button.style.transform = `translate3d(${x * 0.08}px, ${y * 0.11 - 2}px, 0)`;
      });
      button.addEventListener('pointerleave', () => {
        button.style.transform = '';
      });
    });
  }

  const accessMeteorGroups = [...document.querySelectorAll('.access-meteor')];

  if (!reduceMotion && accessMeteorGroups.length) {
    const randomBetween = (min, max) => min + Math.random() * (max - min);
    const meteorStates = accessMeteorGroups.map((meteor, index) => {
      const ring = document.getElementById(meteor.dataset.ring);
      const tail = meteor.querySelector('.meteor-tail');
      const head = meteor.querySelector('circle');
      const length = ring?.getTotalLength?.() ?? 0;
      return {
        meteor,
        ring,
        tail,
        head,
        length,
        tailLength: randomBetween(24, 46),
        startAt: performance.now() + 350 + index * 430,
        duration: randomBetween(1450, 2850),
        opacity: randomBetween(.58, .96)
      };
    }).filter((state) => state.ring && state.length > 0);

    const resetMeteor = (state, now) => {
      state.startAt = now + randomBetween(1200, 5200);
      state.duration = randomBetween(1450, 2850);
      state.opacity = randomBetween(.58, .96);
      state.tailLength = randomBetween(24, 46);
      state.tail?.setAttribute('d', '');
      state.head?.setAttribute('cx', '-100');
      state.head?.setAttribute('cy', '-100');
      state.meteor.style.opacity = '0';
    };

    const animateMeteors = (now) => {
      meteorStates.forEach((state) => {
        if (now < state.startAt) return;
        const progress = (now - state.startAt) / state.duration;
        if (progress >= 1) {
          resetMeteor(state, now);
          return;
        }

        const eased = 1 - Math.pow(1 - progress, 2.15);
        const distance = state.length * (.04 + eased * .92);
        const point = state.ring.getPointAtLength(distance);
        const tailStart = Math.max(0, distance - state.tailLength);
        const sampleCount = 12;
        const tailPoints = [];
        for (let sample = 0; sample <= sampleCount; sample += 1) {
          const sampleDistance = tailStart + (distance - tailStart) * (sample / sampleCount);
          const samplePoint = state.ring.getPointAtLength(sampleDistance);
          tailPoints.push(`${sample === 0 ? 'M' : 'L'} ${samplePoint.x.toFixed(2)} ${samplePoint.y.toFixed(2)}`);
        }
        const fadeIn = Math.min(progress / .16, 1);
        const fadeOut = Math.min((1 - progress) / .2, 1);
        state.tail?.setAttribute('d', tailPoints.join(' '));
        state.head?.setAttribute('cx', point.x.toFixed(2));
        state.head?.setAttribute('cy', point.y.toFixed(2));
        state.meteor.style.opacity = String(Math.max(0, fadeIn * fadeOut * state.opacity));
      });
      requestAnimationFrame(animateMeteors);
    };

    requestAnimationFrame(animateMeteors);
  }

  const sectionLinks = [...document.querySelectorAll('.nav-links a[href^="#"]')];
  const observedSections = sectionLinks
    .map((link) => document.querySelector(link.getAttribute('href')))
    .filter(Boolean);

  const sectionObserver = new IntersectionObserver(
    (entries) => {
      const visible = entries
        .filter((entry) => entry.isIntersecting)
        .sort((a, b) => b.intersectionRatio - a.intersectionRatio)[0];
      if (!visible) return;

      sectionLinks.forEach((link) => {
        link.classList.toggle('active', link.getAttribute('href') === `#${visible.target.id}`);
      });
    },
    { rootMargin: '-35% 0px -55% 0px', threshold: [0, 0.1, 0.25] }
  );
  observedSections.forEach((section) => sectionObserver.observe(section));

  const year = document.querySelector('[data-year]');
  if (year) year.textContent = new Date().getFullYear();
})();
