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
