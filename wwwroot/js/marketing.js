// ── CENTRAL DE INTELIGÊNCIA DE MARKETING ─────────────────────
// Renderiza os gráficos (Chart.js) a partir de <script type="application/json" data-chart="id">.

(function () {
    if (typeof Chart === 'undefined') return;

    const css = getComputedStyle(document.documentElement);
    const primaria = css.getPropertyValue('--primary').trim() || '#6366f1';
    const paleta = [primaria, '#3b82f6', '#10b981', '#f59e0b', '#ef4444', '#8b5cf6', '#14b8a6', '#ec4899', '#64748b'];
    const fmt = new Intl.NumberFormat('pt-BR');

    Chart.defaults.font.family = "'Inter', sans-serif";
    Chart.defaults.font.size = 11;
    Chart.defaults.color = '#64748b';

    document.querySelectorAll('script[data-chart]').forEach(el => {
        const g = JSON.parse(el.textContent);
        const canvas = document.getElementById('chart-' + el.dataset.chart);
        if (!canvas) return;

        const rosca = g.tipo === 'doughnut';
        const temEixo2 = g.series.some(s => s.eixo === 'y2');
        const datasets = g.series.map((s, i) => {
            const cor = s.cor || paleta[i % paleta.length];
            return rosca
                ? { label: s.rotulo, data: s.valores, backgroundColor: g.rotulos.map((_, j) => paleta[j % paleta.length]), borderWidth: 0 }
                : {
                    label: s.rotulo, data: s.valores, borderColor: cor, backgroundColor: g.tipo === 'bar' ? cor + 'cc' : cor + '22',
                    fill: g.tipo === 'line' && g.series.length === 1, tension: .3, pointRadius: g.rotulos.length > 45 ? 0 : 2,
                    borderWidth: 2, borderRadius: 4, spanGaps: true, yAxisID: s.eixo === 'y2' ? 'y2' : 'y'
                };
        });

        const escalas = rosca ? {} : {
            x: { grid: { display: false }, ticks: { maxTicksLimit: 10 } },
            y: { beginAtZero: true, grid: { color: '#eef0f6' }, ticks: { callback: v => fmt.format(v) } },
            ...(temEixo2 ? { y2: { beginAtZero: true, position: 'right', grid: { display: false }, ticks: { callback: v => fmt.format(v) } } } : {})
        };

        new Chart(canvas, {
            type: g.tipo,
            data: { labels: g.rotulos, datasets },
            options: {
                responsive: true, maintainAspectRatio: false,
                interaction: { mode: rosca ? 'nearest' : 'index', intersect: false },
                plugins: {
                    legend: { display: rosca || g.series.length > 1, position: 'bottom', labels: { usePointStyle: true, boxWidth: 8 } },
                    tooltip: { callbacks: { label: c => `${rosca ? c.label : c.dataset.label}: ${c.raw == null ? '—' : fmt.format(c.raw)}` } }
                },
                scales: escalas
            }
        });
    });

    // Período personalizado: mostra/esconde as datas
    const sel = document.getElementById('mktPeriodo');
    const datas = document.getElementById('mktDatas');
    if (sel && datas) {
        const alternar = () => datas.classList.toggle('d-none', sel.value !== 'personalizado');
        sel.addEventListener('change', alternar);
        alternar();
    }
})();
