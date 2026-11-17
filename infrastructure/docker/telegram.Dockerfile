FROM node:22-alpine AS build
WORKDIR /app
COPY services/telegram/package.json services/telegram/package-lock.json ./
RUN npm ci
COPY services/telegram/tsconfig.json ./
COPY services/telegram/src ./src
RUN npm run build

FROM node:22-alpine
WORKDIR /app
ENV NODE_ENV=production PORT=4200
COPY services/telegram/package.json services/telegram/package-lock.json ./
RUN npm ci --omit=dev
COPY --from=build /app/dist ./dist
USER node
EXPOSE 4200
CMD ["node", "dist/index.js"]
