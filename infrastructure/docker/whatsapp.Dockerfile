FROM node:22-alpine AS build
WORKDIR /app
COPY services/whatsapp/package.json services/whatsapp/package-lock.json ./
RUN npm ci
COPY services/whatsapp/tsconfig.json ./
COPY services/whatsapp/src ./src
RUN npm run build

FROM node:22-alpine
WORKDIR /app
ENV NODE_ENV=production PORT=4100
COPY services/whatsapp/package.json services/whatsapp/package-lock.json ./
RUN npm ci --omit=dev
COPY --from=build /app/dist ./dist
USER node
EXPOSE 4100
CMD ["node", "dist/index.js"]
