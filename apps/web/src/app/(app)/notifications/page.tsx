"use client";
import { useEffect, useSyncExternalStore } from "react";
import { NotificationItem } from "@/components/NotificationItem";
import { EmptyState, PageHeader, tileClass } from "@/components/ui";
import { getNotifications, markAllRead, subscribeNotifications } from "@/lib/notifications";

export default function NotificationsPage() {
  const notifications = useSyncExternalStore(subscribeNotifications, getNotifications, () => []);
  // Opening the full list counts as having seen everything.
  useEffect(() => markAllRead(), [notifications]);

  return (
    <div>
      <PageHeader title="Notifications" />
      {notifications.length === 0 ? (
        <EmptyState>Nothing yet. New WhatsApp and Telegram messages will show up here.</EmptyState>
      ) : (
        <ul className={`p-2 ${tileClass}`}>
          {notifications.map((n) => (
            <li key={n.id}><NotificationItem n={n} /></li>
          ))}
        </ul>
      )}
    </div>
  );
}
