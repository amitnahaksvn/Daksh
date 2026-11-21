# Daksha shopping helper

Runs **on the user's own computer** and drives their own Chrome (Playwright) to add items to their cart on Amazon.in and Flipkart.
It exists because neither store offers a way for another app to act on a buyer's account.

- **Sign-in:** a visible Chrome window opens at the store's login page. The user signs in themselves (password, codes, captchas). Daksha never sees the credentials.
  The browser profile stays in `~/.daksh/shopping/<userId>/<store>` on this computer. It is never sent to the server or stored in the database.
- **Add to cart:** headless Chrome, using that profile: search, open the best match, press *Add to cart*, read the cart back.
- **Never checkout:** there is no checkout, order or payment code in the adapters. The user reviews the cart and pays on the store.
- Only the .NET API talks to it (`X-Internal-Key`), and it listens on `127.0.0.1` only.

```
cd services/shopping
npm install
npm run build
npm start          # reads .env (INTERNAL_SERVICE_KEY must equal Integrations__Shopping__InternalServiceKey on the API)
```

API settings: `Integrations__Shopping__BaseUrl` (default `http://127.0.0.1:4300`) and `Integrations__Shopping__InternalServiceKey`.

Supported stores: Amazon.in (tested), Flipkart (best effort; its pages change often). The other shopping apps are list-and-search-link only.
Stores can show captchas or ask for a code; the helper reports that instead of guessing.
