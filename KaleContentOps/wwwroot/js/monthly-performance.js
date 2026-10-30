/* ==========================================================================
   Creative Performance - Monthly Report charts.

   Renders an inline SVG line chart (Reach & Video Views) and a diverging bar
   chart (Net Follower) from the BACKEND payload embedded in
   <script type="application/json" id="cpdChartPayload"> - the exact same
   month rows the tables render. There is no hard-coded production data here
   and no chart library: null values are skipped (gaps / no bar), never
   plotted as zero.
   Axis labels are month-based ("Sep", "Sep 26") instead of day-based.
   ========================================================================== */
(function () {
    'use strict';

    var payloadElement = document.getElementById('cpdChartPayload');
    var lineHost = document.getElementById('cpdLineChart');
    var barHost = document.getElementById('cpdNetChart');

    if (!payloadElement || (!lineHost && !barHost)) {
        return;
    }

    var payload;
    try {
        payload = JSON.parse(payloadElement.textContent || '{}');
    } catch (error) {
        return; // Malformed payload: keep the server-rendered empty states.
    }

    // Oldest -> newest, so both charts share one x axis regardless of the payload order.
    var trend = (payload && Array.isArray(payload.trend)) ? payload.trend.slice() : [];
    trend.sort(function (a, b) {
        return a.date < b.date ? -1 : a.date > b.date ? 1 : 0;
    });

    if (!trend.length) {
        return;
    }

    var SVG_NS = 'http://www.w3.org/2000/svg';
    var MONTHS = ['Januari', 'Februari', 'Maret', 'April', 'Mei', 'Juni',
        'Juli', 'Agustus', 'September', 'Oktober', 'November', 'Desember'];
    var MONTHS_SHORT = ['Jan', 'Feb', 'Mar', 'Apr', 'Mei', 'Jun',
        'Jul', 'Agu', 'Sep', 'Okt', 'Nov', 'Des'];

    var COLORS = {
        reach: '#4d9fff',
        views: '#7c8cff',
        positive: '#45d483',
        negative: '#f47777'
    };

    function svgElement(tag, attributes) {
        var node = document.createElementNS(SVG_NS, tag);
        for (var key in attributes) {
            if (Object.prototype.hasOwnProperty.call(attributes, key)) {
                node.setAttribute(key, attributes[key]);
            }
        }
        return node;
    }

    function numeric(value) {
        return typeof value === 'number' && isFinite(value) ? value : null;
    }

    function formatNumber(value) {
        return Number(value).toLocaleString('id-ID');
    }

    function parts(iso) {
        return String(iso).split('-');
    }

    // Axis label: "Sep" within one year, "Sep 26" when the series crosses years.
    function formatShortDate(iso) {
        var p = parts(iso);
        var monthIndex = Number(p[1]) - 1;
        var years = {};
        trend.forEach(function (point) { years[parts(point.date)[0]] = true; });
        var distinctYears = Object.keys(years).length;
        return distinctYears > 1
            ? MONTHS_SHORT[monthIndex] + " '" + String(p[0]).slice(2)
            : MONTHS_SHORT[monthIndex];
    }

    function formatFullDate(iso) {
        var p = parts(iso);
        return MONTHS[Number(p[1]) - 1] + ' ' + p[0];
    }

    // Axis ticks: compact Indonesian labels (0 / 5rb / 10rb / 15rb / 20rb).
    function niceMax(value) {
        if (!(value > 0)) {
            return 1;
        }
        var magnitude = Math.pow(10, Math.floor(Math.log10(value)));
        var scaled = value / magnitude;
        var step = scaled <= 1 ? 1 : scaled <= 2 ? 2 : scaled <= 5 ? 5 : 10;
        return step * magnitude;
    }

    function tickLabel(value) {
        if (Math.abs(value) >= 1000) {
            var thousands = Math.round((value / 1000) * 10) / 10;
            return (thousands % 1 === 0 ? thousands.toFixed(0) : thousands.toFixed(1)) + 'rb';
        }
        return String(Math.round(value));
    }

    function tooltipRow(color, label, value, prefix) {
        var text = value === null ? '\u2014' : (prefix || '') + formatNumber(value);
        return '<div class="cpd-tooltip-row"><span class="cpd-tooltip-swatch" style="background:' + color + '"></span>' +
            label + ': ' + text + '</div>';
    }

    function attachTooltip(host, svg, candidateIndexes, xFor, chartWidth, htmlFor) {
        var tooltip = document.createElement('div');
        tooltip.className = 'cpd-tooltip';
        tooltip.style.display = 'none';
        host.appendChild(tooltip);

        svg.addEventListener('mousemove', function (event) {
            var rect = svg.getBoundingClientRect();
            if (!rect.width || !candidateIndexes.length) {
                return;
            }

            var scale = chartWidth / rect.width;
            var mouseX = (event.clientX - rect.left) * scale;

            var bestIndex = candidateIndexes[0];
            var bestDistance = Infinity;
            candidateIndexes.forEach(function (index) {
                var distance = Math.abs(xFor(index) - mouseX);
                if (distance < bestDistance) {
                    bestDistance = distance;
                    bestIndex = index;
                }
            });

            tooltip.innerHTML = htmlFor(bestIndex);
            tooltip.style.display = 'block';
            tooltip.style.left = (xFor(bestIndex) / scale) + 'px';
            tooltip.style.top = '18px';
        });

        svg.addEventListener('mouseleave', function () {
            tooltip.style.display = 'none';
        });
    }

    function renderLineChart(host) {
        var hasLineData = trend.some(function (point) {
            return numeric(point.reach) !== null || numeric(point.videoViews) !== null;
        });
        if (!hasLineData) {
            return false; // keep the server-rendered empty state
        }

        var width = 1000;
        var height = 300;
        var padLeft = 54;
        var padRight = 14;
        var padTop = 14;
        var padBottom = 40;

        var maxValue = 0;
        trend.forEach(function (point) {
            maxValue = Math.max(maxValue, numeric(point.reach) || 0, numeric(point.videoViews) || 0);
        });
        var yMax = niceMax(maxValue);

        var xFor = function (index) {
            if (trend.length === 1) {
                return (padLeft + (width - padRight)) / 2;
            }
            return padLeft + index * (width - padLeft - padRight) / (trend.length - 1);
        };
        var yFor = function (value) {
            return height - padBottom - (value / yMax) * (height - padTop - padBottom);
        };

        var svg = svgElement('svg', {
            viewBox: '0 0 ' + width + ' ' + height,
            role: 'img',
            'aria-label': 'Tren Reach dan Video Views bulanan'
        });

        for (var tick = 0; tick <= 4; tick++) {
            var tickValue = (yMax * tick) / 4;
            var tickY = yFor(tickValue);
            svg.appendChild(svgElement('line', {
                x1: padLeft, x2: width - padRight, y1: tickY, y2: tickY, 'class': 'cpd-chart-grid'
            }));
            var tickText = svgElement('text', {
                x: padLeft - 8, y: tickY + 4, 'class': 'cpd-chart-axis-label', 'text-anchor': 'end'
            });
            tickText.textContent = tickLabel(tickValue);
            svg.appendChild(tickText);
        }

        var labelStep = Math.max(1, Math.ceil(trend.length / 7));
        trend.forEach(function (point, index) {
            if (index % labelStep !== 0 && index !== trend.length - 1) {
                return;
            }
            var label = svgElement('text', {
                x: xFor(index), y: height - padBottom + 20, 'class': 'cpd-chart-axis-label', 'text-anchor': 'middle'
            });
            label.textContent = formatShortDate(point.date);
            svg.appendChild(label);
        });

        // Gaps: a null value breaks the line instead of being drawn as zero.
        [['reach', 'reach', COLORS.reach], ['videoViews', 'views', COLORS.views]].forEach(function (series) {
            var key = series[0];
            var cssClass = series[1];
            var segments = [];
            var currentSegment = [];

            trend.forEach(function (point, index) {
                var value = numeric(point[key]);
                if (value === null) {
                    if (currentSegment.length) {
                        segments.push(currentSegment);
                        currentSegment = [];
                    }
                    return;
                }
                currentSegment.push({ index: index, value: value });
            });
            if (currentSegment.length) {
                segments.push(currentSegment);
            }

            segments.forEach(function (segment) {
                if (segment.length === 1) {
                    // SINGLE-POINT DATASET (a period covered by one month, e.g. 1-29
                    // Oktober): no line may be invented for one point, but the marker
                    // itself must be unmistakable - a soft halo plus a larger dot of
                    // the same series colour. Values are read from the payload as-is.
                    var lone = segment[0];
                    var loneX = xFor(lone.index).toFixed(2);
                    var loneY = yFor(lone.value).toFixed(2);
                    svg.appendChild(svgElement('circle', {
                        cx: loneX,
                        cy: loneY,
                        r: 10,
                        'class': 'cpd-chart-point-halo ' + cssClass
                    }));
                    svg.appendChild(svgElement('circle', {
                        cx: loneX,
                        cy: loneY,
                        r: 6,
                        'class': 'cpd-chart-point ' + cssClass
                    }));
                    return;
                }

                var path = segment.map(function (point, position) {
                    return (position === 0 ? 'M' : 'L') +
                        xFor(point.index).toFixed(2) + ' ' + yFor(point.value).toFixed(2);
                }).join(' ');
                svg.appendChild(svgElement('path', { d: path, 'class': 'cpd-chart-line ' + cssClass }));

                segment.forEach(function (point) {
                    svg.appendChild(svgElement('circle', {
                        cx: xFor(point.index).toFixed(2),
                        cy: yFor(point.value).toFixed(2),
                        r: 2.6,
                        'class': 'cpd-chart-point ' + cssClass
                    }));
                });
            });
        });

        var candidates = [];
        trend.forEach(function (point, index) {
            if (numeric(point.reach) !== null || numeric(point.videoViews) !== null) {
                candidates.push(index);
            }
        });

        attachTooltip(host, svg, candidates, xFor, width, function (index) {
            var point = trend[index];
            return '<strong>' + formatFullDate(point.date) + '</strong>' +
                tooltipRow(COLORS.reach, 'Reach', numeric(point.reach)) +
                tooltipRow(COLORS.views, 'Video Views', numeric(point.videoViews));
        });

        host.textContent = '';
        host.appendChild(svg);
        return true;
    }

    function renderBarChart(host) {
        var hasBarData = trend.some(function (point) {
            return numeric(point.netFollower) !== null;
        });
        if (!hasBarData) {
            return false; // keep the server-rendered empty state
        }

        var width = 1000;
        var height = 230;
        var padLeft = 54;
        var padRight = 14;
        var padTop = 16;
        var padBottom = 36;

        var maxAbs = 0;
        trend.forEach(function (point) {
            var value = numeric(point.netFollower);
            if (value !== null) {
                maxAbs = Math.max(maxAbs, Math.abs(value));
            }
        });
        var limit = niceMax(maxAbs);

        var innerHeight = height - padTop - padBottom;
        var yFor = function (value) {
            return padTop + innerHeight / 2 - (value / limit) * (innerHeight / 2);
        };
        var band = (width - padLeft - padRight) / trend.length;
        var barWidth = Math.min(38, Math.max(8, band * 0.55));
        var xFor = function (index) {
            return padLeft + band * index + band / 2;
        };

        var svg = svgElement('svg', {
            viewBox: '0 0 ' + width + ' ' + height,
            role: 'img',
            'aria-label': 'Pergerakan Net Follower bulanan'
        });

        [limit, 0, -limit].forEach(function (value) {
            var lineY = yFor(value);
            svg.appendChild(svgElement('line', {
                x1: padLeft,
                x2: width - padRight,
                y1: lineY,
                y2: lineY,
                'class': value === 0 ? 'cpd-chart-baseline' : 'cpd-chart-grid'
            }));
            var label = svgElement('text', {
                x: padLeft - 8, y: lineY + 4, 'class': 'cpd-chart-axis-label', 'text-anchor': 'end'
            });
            label.textContent = (value > 0 ? '+' : '') + tickLabel(value);
            svg.appendChild(label);
        });

        var labelStep = Math.max(1, Math.ceil(trend.length / 7));
        trend.forEach(function (point, index) {
            var value = numeric(point.netFollower);
            if (value !== null) {
                var zeroY = yFor(0);
                var valueY = yFor(value);
                svg.appendChild(svgElement('rect', {
                    x: (xFor(index) - barWidth / 2).toFixed(2),
                    y: Math.min(zeroY, valueY).toFixed(2),
                    width: barWidth.toFixed(2),
                    height: Math.max(1.5, Math.abs(zeroY - valueY)).toFixed(2),
                    rx: 2,
                    'class': value >= 0 ? 'cpd-chart-bar pos' : 'cpd-chart-bar neg'
                }));
            }

            if (index % labelStep !== 0 && index !== trend.length - 1) {
                return;
            }
            var label = svgElement('text', {
                x: xFor(index).toFixed(2), y: height - padBottom + 20, 'class': 'cpd-chart-axis-label', 'text-anchor': 'middle'
            });
            label.textContent = formatShortDate(point.date);
            svg.appendChild(label);
        });

        var candidates = [];
        trend.forEach(function (point, index) {
            if (numeric(point.netFollower) !== null) {
                candidates.push(index);
            }
        });

        attachTooltip(host, svg, candidates, xFor, width, function (index) {
            var point = trend[index];
            var value = numeric(point.netFollower);
            return '<strong>' + formatFullDate(point.date) + '</strong>' +
                tooltipRow(value >= 0 ? COLORS.positive : COLORS.negative, 'Net Follower', value, value > 0 ? '+' : '');
        });

        host.textContent = '';
        host.appendChild(svg);
        return true;
    }

    if (lineHost) {
        renderLineChart(lineHost);
    }
    if (barHost) {
        renderBarChart(barHost);
    }
})();
