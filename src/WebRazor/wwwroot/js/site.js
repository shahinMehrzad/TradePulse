// A read-only box with data-select-on-click selects all of its text when clicked, for copying (inline handlers are blocked by the content security policy).
document.addEventListener('click', function (event) {
    var box = event.target.closest && event.target.closest('[data-select-on-click]');
    if (box) {
        box.select();
    }
});

// The eye in a password field shows the text only while it is held down (mouse, touch or the space bar) and hides it again on release.
(function () {
    function reveal(button, show) {
        button.parentElement.querySelector('input').type = show ? 'text' : 'password';
    }

    document.addEventListener('pointerdown', function (event) {
        var button = event.target.closest && event.target.closest('[data-reveal-password]');
        if (button) {
            event.preventDefault(); // keep the focus (and the caret) in the text box
            reveal(button, true);
        }
    });

    ['pointerup', 'pointercancel', 'pointerleave', 'blur'].forEach(function (type) {
        document.addEventListener(type, function () {
            document.querySelectorAll('[data-reveal-password]').forEach(function (button) { reveal(button, false); });
        }, true);
    });

    document.addEventListener('keydown', function (event) {
        var button = event.target.closest && event.target.closest('[data-reveal-password]');
        if (button && (event.key === ' ' || event.key === 'Enter')) {
            event.preventDefault();
            reveal(button, true);
        }
    });

    document.addEventListener('keyup', function (event) {
        var button = event.target.closest && event.target.closest('[data-reveal-password]');
        if (button) {
            reveal(button, false);
        }
    });
})();

// Password fields: a checklist (data-password-rules) ticks off the rules of the new password live.
(function () {
    var list = document.querySelector('[data-password-rules]');
    if (!list) {
        return;
    }

    var password = document.getElementById(list.getAttribute('data-password-rules'));
    var repeat = document.getElementById(list.getAttribute('data-repeat'));
    var rules = {
        length: function (v) { return v.length >= 12; },
        lower: function (v) { return /[a-z]/.test(v); },
        upper: function (v) { return /[A-Z]/.test(v); },
        digit: function (v) { return /[0-9]/.test(v); },
        symbol: function (v) { return /[^A-Za-z0-9]/.test(v); },
        match: function (v) { return v.length > 0 && repeat && repeat.value === v; }
    };

    function refresh() {
        list.querySelectorAll('[data-rule]').forEach(function (item) {
            var ok = rules[item.getAttribute('data-rule')](password.value);
            item.classList.toggle('text-success', ok);
            item.classList.toggle('text-secondary', !ok);
            item.firstElementChild.textContent = ok ? '✓' : '○';
        });
    }

    password.addEventListener('input', refresh);
    if (repeat) {
        repeat.addEventListener('input', refresh);
    }
    refresh();
})();

// A form with data-confirm asks before it is sent (inline onsubmit handlers are blocked by the panel's content security policy).
document.addEventListener('submit', function (event) {
    var message = event.target.getAttribute && event.target.getAttribute('data-confirm');
    if (message && !window.confirm(message)) {
        event.preventDefault();
    }
});

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
