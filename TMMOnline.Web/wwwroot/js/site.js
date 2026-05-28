(() => {
    const header = document.querySelector('.site-header');
    if (!header) {
        return;
    }

    let lastY = window.scrollY;
    window.addEventListener('scroll', () => {
        const currentY = window.scrollY;
        if (currentY > lastY && currentY > 100) {
            header.style.transform = 'translateY(-100%)';
        } else {
            header.style.transform = 'translateY(0)';
        }
        lastY = currentY;
    });
})();

(() => {
    const rotators = document.querySelectorAll('[data-top-ad-rotator="true"]');
    if (!rotators.length) {
        return;
    }

    const showItem = (items, indexToShow) => {
        items.forEach((item, i) => {
            const isActive = i === indexToShow;
            item.classList.toggle('is-active', isActive);
            item.setAttribute('aria-hidden', isActive ? 'false' : 'true');
        });
    };

    rotators.forEach((rotator) => {
        const items = Array.from(rotator.querySelectorAll('[data-top-ad-item="true"]'));
        if (items.length <= 1) {
            return;
        }

        const intervalMs = Number(rotator.getAttribute('data-rotate-ms')) || 10000;
        let index = items.findIndex((item) => item.classList.contains('is-active'));
        if (index < 0) {
            index = 0;
        }

        showItem(items, index);

        window.setInterval(() => {
            index = (index + 1) % items.length;
            showItem(items, index);
        }, intervalMs);
    });
})();
