import 'package:daksh_mobile/core/api_client.dart';
import 'package:daksh_mobile/features/calendar_screen.dart';
import 'package:flutter_test/flutter_test.dart';

void main() {
  test('an event reads its day and clock time from the owner-timezone text', () {
    final e = CalendarEvent.fromJson({'id': 'a', 'title': 'Dentist', 'start': '2026-11-20T17:05:00+05:30', 'allDay': false, 'location': null});
    expect(e.day, '2026-11-20');
    expect(e.time, '5:05 PM');
    expect(CalendarEvent.fromJson({'id': 'b', 'title': 'Late', 'start': '2026-11-20T00:00:00+05:30', 'allDay': false}).time, '12:00 AM');
    expect(CalendarEvent.fromJson({'id': 'c', 'title': 'Holiday', 'start': '2026-11-21', 'allDay': true}).time, 'All day');
  });

  test('the calendar response keeps the not-connected state', () {
    final d = CalendarData.fromJson({'connected': false, 'message': 'x', 'today': '2026-11-20', 'events': []});
    expect(d.connected, isFalse);
    expect(d.events, isEmpty);
  });

  test('accounts without calendar access are listed as not shown, the rest can be added to', () {
    final d = CalendarData.fromJson({
      'connected': true, 'today': '2026-11-20', 'events': [],
      'accounts': [
        {'id': 'a', 'email': 'work@x.com', 'hasCalendar': true},
        {'id': 'b', 'email': 'old@x.com', 'hasCalendar': false},
        {'id': 'c', 'email': 'flaky@x.com', 'hasCalendar': true, 'error': 'boom'},
      ],
    });
    expect(d.usable.map((a) => a.email), ['work@x.com', 'flaky@x.com']);
    expect(d.notShown.map((a) => a.email), ['old@x.com', 'flaky@x.com']);
    expect(CalendarEvent.fromJson({'id': 'e', 'title': 'T', 'start': '2026-11-20', 'allDay': true, 'accountId': 'a', 'account': 'work@x.com'}).account, 'work@x.com');
  });

  test('an event knows its length for the edit form', () {
    final e = CalendarEvent.fromJson({'id': 'a', 'title': 'T', 'start': '2026-11-20T17:00:00+05:30', 'end': '2026-11-20T18:30:00+05:30', 'allDay': false});
    expect(e.minutes, 90);
    expect(CalendarEvent.fromJson({'id': 'b', 'title': 'T', 'start': '2026-11-20', 'end': '2026-11-21', 'allDay': true}).minutes, 60);
  });

  test('day headings mark today', () {
    expect(dayHeading('2026-11-20', '2026-11-20'), 'Today · Fri 20 Nov');
    expect(dayHeading('2026-11-21', '2026-11-20'), 'Sat 21 Nov');
  });
}
