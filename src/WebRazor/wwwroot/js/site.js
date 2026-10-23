// Offers only the choices the API would accept. The rules are not written here: the API publishes them (market-data-support),
// the page embeds them as JSON, and this script only reads them. Without the script nothing breaks, the API still refuses
// an impossible choice and the page shows its reason.
(function () {
    var source = document.getElementById('market-data-support');
    if (!source) {
        return;
    }

    var support;
    try {
        support = JSON.parse(source.textContent);
    } catch (e) {
        return;
    }

    // Data without a time frame lists none (null); data split by time frame needs the chosen one to be listed.
    function supports(exchange, market, dataType, timeFrame) {
        return support.some(function (entry) {
            return entry.exchangeId === exchange && entry.marketId === market && entry.dataType === dataType &&
                (entry.timeFrameIds === null || (timeFrame !== null && entry.timeFrameIds.indexOf(timeFrame) !== -1));
        });
    }

    document.querySelectorAll('[data-support-form]').forEach(function (form) {
        function chosen(name) {
            var field = form.querySelector('[data-support-field="' + name + '"]');
            return field ? (parseInt(field.value, 10) || 0) : null;
        }

        function refresh() {
            var exchange = chosen('exchange');
            var market = chosen('market');
            var timeFrame = chosen('timeframe');

            form.querySelectorAll('[data-support-type]').forEach(function (box) {
                var available = supports(exchange, market, box.getAttribute('data-support-type'), timeFrame);
                box.disabled = !available;
                if (!available) {
                    box.checked = false;
                }
            });

            // A field that belongs to a checkbox (the funding rate's start date) is usable only while that box is ticked.
            form.querySelectorAll('[data-follows]').forEach(function (field) {
                var box = form.querySelector(field.getAttribute('data-follows'));
                field.disabled = !(box && box.checked && !box.disabled);
            });
        }

        form.addEventListener('change', refresh);
        refresh();
    });
})();
