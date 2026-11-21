FROM node:22-bookworm-slim AS build
WORKDIR /app
COPY services/shopping/package.json services/shopping/package-lock.json ./
RUN npm ci
COPY services/shopping/tsconfig.json ./
COPY services/shopping/src ./src
RUN npm run build

FROM node:22-bookworm-slim
WORKDIR /app
ENV NODE_ENV=production PORT=4300 HOST=0.0.0.0 PLAYWRIGHT_BROWSERS_PATH=/ms-playwright
COPY services/shopping/package.json services/shopping/package-lock.json ./
RUN npm ci --omit=dev \
 && npx playwright-core install --with-deps chromium \
 && rm -rf /var/lib/apt/lists/* \
 && mkdir -p /data/profiles && chown -R node:node /data /ms-playwright
COPY --from=build /app/dist ./dist
USER node
EXPOSE 4300
CMD ["node", "dist/index.js"]
