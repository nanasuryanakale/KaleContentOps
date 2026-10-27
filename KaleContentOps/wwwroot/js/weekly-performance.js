/* ==========================================================================
   Creative Performance - Weekly Report charts.

   Renders an inline SVG line chart (Reach & Video Views) from the BACKEND payload
   embedded in <script type="application/json" id="cpdChartPayload"> - the exact same rows
   the tables render. There is no hard-coded production data here and no chart
   library: null values are skipped (gaps / no bar), never plotted as zero.
   ========================================================================== */
(function () {
    'use strict';

    var payloadElement = document.getElementById('cpdChartPayload');
    var lineHost = document.getElementById('cpdLineChart');

    if (!payloadElement || !lineHost) {
        return;
    }

    var payload;
    try {
        payload = JSON.parse(payloadElement.textContent || '{}');
    } catch (error) {
        return; // Malformed payload: keep the server-rendered empty states.
    }

    // Oldest -> newest, so charts share one x axis regardless of the payload order.
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

    var COLORS = {
        reach: '#4d9fff',
        views: '#7c8cff',
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

    function pad2(value) {
        return String(value).padStart(2, '0');
    }

    function parts(iso) {
        return String(iso).split('-');
    }

    function formatShortDate(iso) {
        var p = parts(iso);
        return pad2(Number(p[2])) + '/' + pad2(Number(p[1]));
    }

    function formatFullDate(iso) {
        var p = parts(iso);
        return Number(p[2]) + ' ' + MONTHS[Number(p[1]) - 1] + ' ' + p[0];
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
        var text = value === null ? '—' : (prefix || '') + formatNumber(value);
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
            'aria-label': 'Tren Reach dan Video Views mingguan'
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
            // Weekly buckets carry a "dd/MM–dd/MM" range label; fall back to the start date.
            label.textContent = point.label || formatShortDate(point.date);
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
                    svg.appendChild(svgElement('circle', {
                        cx: xFor(segment[0].index).toFixed(2),
                        cy: yFor(segment[0].value).toFixed(2),
                        r: 3.4,
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
            var heading = point.end && point.end !== point.date
                ? formatFullDate(point.date) + ' \u2013 ' + formatFullDate(point.end)
                : formatFullDate(point.date);
            return '<strong>' + heading + '</strong>' +
                tooltipRow(COLORS.reach, 'Reach', numeric(point.reach)) +
                tooltipRow(COLORS.views, 'Video Views', numeric(point.videoViews));
        });

        host.textContent = '';
        host.appendChild(svg);
        return true;
    }

    if (lineHost) {
        renderLineChart(lineHost);
    }
})();