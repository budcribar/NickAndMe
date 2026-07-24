// Lightweight Chart.js helpers for admin LoadSim dashboard
window.filmStudioCharts = {
  _charts: {},
  _chartJsLoading: null,

  ensureChartJs: function () {
    if (window.Chart) return Promise.resolve();
    if (this._chartJsLoading) return this._chartJsLoading;
    this._chartJsLoading = new Promise(function (resolve, reject) {
      var s = document.createElement('script');
      s.src = 'https://cdn.jsdelivr.net/npm/chart.js@4.4.1/dist/chart.umd.min.js';
      s.onload = function () { resolve(); };
      s.onerror = function () { reject(new Error('Chart.js failed to load')); };
      document.head.appendChild(s);
    });
    return this._chartJsLoading;
  },

  /**
   * Upsert a line chart.
   * @param {string} canvasId
   * @param {string[]} labels
   * @param {{label:string, data:number[], color:string, yAxisID?:string}[]} series
   * @param {{yTitle?:string, y2Title?:string, dualY?:boolean}} opts
   */
  upsertLine: async function (canvasId, labels, series, opts) {
    opts = opts || {};
    await this.ensureChartJs();
    var canvas = document.getElementById(canvasId);
    if (!canvas) return;

    var datasets = series.map(function (s) {
      return {
        label: s.label,
        data: s.data,
        borderColor: s.color,
        backgroundColor: s.color + '33',
        tension: 0.3,
        fill: false,
        pointRadius: 0,
        borderWidth: 2,
        yAxisID: s.yAxisID || 'y'
      };
    });

    var scales = {
      x: {
        ticks: { maxTicksLimit: 8, color: '#888', font: { size: 10 } },
        grid: { color: 'rgba(0,0,0,0.05)' }
      },
      y: {
        beginAtZero: true,
        title: { display: !!opts.yTitle, text: opts.yTitle || '' },
        ticks: { color: '#888', font: { size: 10 } },
        grid: { color: 'rgba(0,0,0,0.06)' }
      }
    };
    if (opts.dualY) {
      scales.y1 = {
        position: 'right',
        beginAtZero: true,
        title: { display: !!opts.y2Title, text: opts.y2Title || '' },
        ticks: { color: '#888', font: { size: 10 } },
        grid: { drawOnChartArea: false }
      };
    }

    if (this._charts[canvasId]) {
      var ch = this._charts[canvasId];
      ch.data.labels = labels;
      ch.data.datasets = datasets;
      ch.update('none');
      return;
    }

    this._charts[canvasId] = new Chart(canvas.getContext('2d'), {
      type: 'line',
      data: { labels: labels, datasets: datasets },
      options: {
        responsive: true,
        maintainAspectRatio: false,
        animation: false,
        interaction: { mode: 'index', intersect: false },
        plugins: {
          legend: { position: 'bottom', labels: { boxWidth: 12, font: { size: 11 } } }
        },
        scales: scales
      }
    });
  },

  destroy: function (canvasId) {
    if (this._charts[canvasId]) {
      this._charts[canvasId].destroy();
      delete this._charts[canvasId];
    }
  }
};
