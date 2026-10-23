// Choosing a time frame on a card swaps just that card for the server's new HTML (the Card handler of the dashboard page).
// Without this script the picker simply does nothing and the card stays on its default time frame; the rest of the page works.
(function () {
    var grid = document.querySelector('[data-series-grid]');
    if (!grid) {
        return;
    }

    grid.addEventListener('change', function (event) {
        var select = event.target;
        if (!select.matches('[data-card-timeframe]')) {
            return;
        }

        var card = select.closest('[data-series-card]');
        var previous = select.getAttribute('data-current');
        var query = new URLSearchParams({
            handler: 'Card',
            exchange: card.getAttribute('data-exchange'),
            market: card.getAttribute('data-market'),
            symbol: card.getAttribute('data-symbol'),
            timeframe: select.value
        });

        card.setAttribute('aria-busy', 'true');
        select.disabled = true;

        fetch(window.location.pathname + '?' + query.toString(), { headers: { 'Accept': 'text/html' }, credentials: 'same-origin' })
            .then(function (response) {
                if (response.status === 401) {
                    // The session ended: the page itself sends the admin to the sign-in.
                    window.location.reload();
                    return null;
                }
                if (!response.ok) {
                    throw new Error('The card could not be loaded (' + response.status + ').');
                }
                return response.text();
            })
            .then(function (html) {
                if (html === null) {
                    return;
                }
                var holder = document.createElement('div');
                holder.innerHTML = html.trim();
                var fresh = holder.firstElementChild;
                if (!fresh) {
                    throw new Error('The card came back empty.');
                }
                card.replaceWith(fresh);
            })
            .catch(function () {
                // Put the old choice back: what is on screen is still the old time frame.
                card.removeAttribute('aria-busy');
                select.disabled = false;
                select.value = previous;
                select.classList.add('is-invalid');
                window.setTimeout(function () { select.classList.remove('is-invalid'); }, 2500);
            });
    });
})();
