from __future__ import annotations

import argparse
import json
import os
from datetime import date, datetime, time, timedelta
from decimal import Decimal
from pathlib import Path
from zoneinfo import ZoneInfo

import json5
import psycopg
from psycopg import sql

from generate import Generator, money


class CalendarData(Generator):
    def __init__(self, db, start, end, seed):
        super().__init__(db, seed, (end - start).days + 1,
                         datetime.combine(end, time(23, 59), ZoneInfo('Europe/Istanbul')))
        self.calendar_start, self.calendar_end = start, end
        self.allow_debt = False
        self.staff = ['admin']
        self.position = 0
        self.today = db.execute("select (current_timestamp at time zone 'Europe/Istanbul')::date").fetchone()[0]

    def prepare(self):
        self.db.execute("select pg_advisory_xact_lock(hashtext('synthetic_calendar_extension'))")
        if self.db.execute("select exists(select 1 from prescription where medula_prescription_no like 'CAL%' or medula_prescription_no like 'VAR%')").fetchone()[0]:
            raise RuntimeError('Calendar data already exists; refusing to duplicate it.')
        self.db.execute("select set_config('pharmacy.user_id','admin',true)")
        self.db.execute(Path(__file__).resolve().parents[2].joinpath('database/074_hide_future_activity.sql').read_text(encoding='utf-8'))
        self.reset_sequences()

        self.movement_types = dict(self.db.execute('select code,record_id from "qrMovementTypes"'))
        self.diagnoses = [r[0] for r in self.db.execute('select code from icd10_diagnosis order by code')]
        self.invoice_types = [r[0] for r in self.db.execute('select invoice_type_code from prescription_invoice_type where return_status_code=0 order by 1')]
        with self.db.cursor(row_factory=psycopg.rows.dict_row) as cur:
            self.doctors = cur.execute('select * from doctor order by record_id').fetchall()
            self.depots = cur.execute('select * from depot order by record_id').fetchall()
            self.pharmacies = cur.execute('select * from pharmacy order by record_id').fetchall()
            self.patient_rows = {p['record_id']: p for p in cur.execute('select * from patient order by record_id')}
            available = cur.execute('''select b.record_id,b.barcode,b.medicine_name,p.sale_price,p.vat_rate
                from barcode b join "barcodePrices" p on p.record_reference_no=b.record_id
                where b.barcode ~ '^[0-9]{13}$' and p.sale_price>0 order by b.record_id''').fetchall()
        used = {r[0] for r in self.db.execute('select distinct pharmacy_qr_barcode(qr_code) from "qrMovements"')}
        existing = [m for m in available if m['barcode'] in used]
        target = round(self.db.execute('select count(*) from barcode').fetchone()[0] * .9)
        self.medicines = existing + self.r.sample([m for m in available if m['barcode'] not in used], target - len(existing))
        self.r.shuffle(self.medicines)
        for med in self.medicines:
            med['price'] = money(med['sale_price'])
            med['cost'] = money(med['price'] * Decimal('.72'))
        self.rx_rows = [None] * self.db.execute('select count(*) from prescription').fetchone()[0]
        self.own_gln = self.db.execute("select gln from addresses where party_name not like '%Depot%' and record_id=(select receiver_address_id from invoices where invoice_type='Gelen Fatura' limit 1)").fetchone()[0]
        self.own_address = self.db.execute('select record_id from addresses where gln=%s order by record_id limit 1', (self.own_gln,)).fetchone()[0]
        addresses = dict(self.db.execute('select gln,record_id from addresses where gln is not null order by record_id'))
        self.depot_addresses = {d['record_id']: addresses[d['gln_code']] for d in self.depots}
        self.pharmacy_addresses = {p['record_id']: addresses[p['gln_code']] for p in self.pharmacies}
        print(f'Extending {len(self.medicines)} medicines from {self.calendar_start} through {self.calendar_end}.', flush=True)

    def take(self):
        med = self.medicines[self.position % len(self.medicines)]
        self.position += 1
        box = self.make_qr(med)
        self.line_number += 1
        line = self.insert('invoice_lines', invoice_uuid=self.purchase_id, line_number=self.line_number,
            barcode=med['barcode'], item_name=med['medicine_name'], quantity=1, unit='ADET', unit_price=med['cost'],
            vat_rate=0, vat_amount=0, discount_amount=0, line_amount=med['cost'], currency='TRY', is_medication=True,
            stock_tracking_type='QR', raw_data={'synthetic': True})
        self.insert('invoice_line_qr_codes', invoice_line_id=line['record_id'], qr_code=box['qr'],
            gtin=box['gtin'], barcode=med['barcode'], serial_number=box['serial'], batch_number=box['batch'],
            expiry_date=box['expiry'], raw_code=box['qr'])
        self.move(box, 'DEP_IN', self.current_stamp, 'INV-' + self.purchase_id, self.current_depot['gln_code'])
        if self.current_stamp.date() <= self.today:
            self.db.execute('update "qrActive" set purchase_cost=%s where qr_code=%s', (med['cost'], box['qr']))
        self.purchase_total += med['cost']
        return box

    def run_calendar(self):
        self.prepare()
        patients = list(self.patient_rows)
        day = self.calendar_start
        while day <= self.calendar_end:
            self.current_stamp = datetime.combine(day, time(11), ZoneInfo('Europe/Istanbul'))
            self.current_depot = self.depots[(day - self.calendar_start).days % len(self.depots)]
            self.purchase_id, self.line_number, self.purchase_total = str(self.uid()), 0, Decimal(0)
            self.insert('invoices', invoice_uuid=self.purchase_id, invoice_type='Gelen Fatura',
                sender_address_id=self.depot_addresses[self.current_depot['record_id']], receiver_address_id=self.own_address,
                issue_date=day, due_date=day + timedelta(days=30), database_entry_at=self.current_stamp,
                total_cost=0, discount_amount=0, kdv_amount=0, payable_amount=0, is_saved=True)
            for index in range(30):
                self.sale(self.current_stamp, self.r.choice(patients), prescription=index < 20)
                if index < 20:
                    row = self.rx_rows[-1]
                    number = f'CAL{len(self.rx_rows):07}'
                    self.db.execute('update prescription set medula_prescription_no=%s where prescription_uid=%s', (number, row['prescription_uid']))
                    self.db.execute('update sales set prescription_no=%s where prescription_uid=%s', (number, row['prescription_uid']))
            if day <= self.today:
                # Retain present/past stock, but never apply a future purchase to today's stock.
                for _ in range(30):
                    self.take()
            self.db.execute('update invoices set total_cost=%s,payable_amount=%s where invoice_uuid=%s',
                (self.purchase_total, self.purchase_total, self.purchase_id))
            log = self.insert('invoice_stock_logs', invoice_uuid=self.purchase_id, action_code='SAVE', occurred_at=self.current_stamp,
                user_id='admin', reference_code='INV-' + self.purchase_id, movement_type_id=self.movement_types['DEP_IN'], notes='')
            self.insert('invoice_stock_log_invoices', invoice_stock_log_id=log['record_id'], invoice_uuid=self.purchase_id)
            if day.day == 1:
                print('Generated through ' + str(day), flush=True)
            day += timedelta(days=1)
        self.db.execute('''update prescription_log log set prescription_snapshot=to_jsonb(p)
            from prescription p where p.prescription_uid=log.prescription_uid and p.medula_prescription_no like 'CAL%' ''')
        self.db.execute('''update loyal_patient_report_supply_evidence evidence set total_report_count=(
            select count(*) from prescription p join patient_identity i on i.tc_identity_no=p.patient_tc_identity_no
            where i.patient_record_id=evidence.patient_record_id and p.saved_at<=evidence.evaluated_at and not p.is_deleted)''')
        self.db.execute('select public.refresh_visible_activity()')
        # Capture the actual actor/date in all append-only snapshots; freeform notes remain blank.
        self.reset_sequences()


def verify_calendar(db, end=date(2026, 12, 31)):
    future = db.execute("select count(*) from prescription where saved_at>clock_timestamp()").fetchone()[0]
    if future == 0 and db.execute('select current_date').fetchone()[0] < end:
        raise AssertionError('Expected scheduled prescriptions through year end.')
    latest = db.execute("select max(prescription_date) from prescription where medula_prescription_no like 'CAL%' or medula_prescription_no like 'VAR%'").fetchone()[0]
    if latest != end:
        raise AssertionError('Calendar does not reach 2026-12-31.')
    bad_totals = db.execute('''select count(*) from sales s where total_amount<>(
        select coalesce(sum(exit_price),0) from "salesQrItems" i where i.sale_id=s.record_id)''').fetchone()[0]
    if bad_totals:
        raise AssertionError('Sale totals do not match line items.')
    db.execute('SET LOCAL ROLE portfolio')
    try:
        for table, column in [('prescription','saved_at'),('sales','timestamp'),('qrMovements','movement_date'),('invoices','database_entry_at')]:
            value = db.execute(sql.SQL('select count(*) from {} where {}>clock_timestamp()').format(sql.Identifier(table),sql.Identifier(column))).fetchone()[0]
            if value:
                raise AssertionError('Application can see future rows: '+table)
        if db.execute('''select count(*) from patient p where prescription_count<>(
            select count(*) from prescription r join patient_identity i on i.tc_identity_no=r.patient_tc_identity_no
            where i.patient_record_id=p.record_id and not r.is_deleted)''').fetchone()[0]:
            raise AssertionError('Visible patient prescription counts are incorrect.')
        if db.execute('''select count(*) from "barcodeActive" b where quantity<>(
            select count(*) from "qrActive" q where q.barcode=b.barcode)''').fetchone()[0]:
            raise AssertionError('Current stock includes scheduled inventory.')
        used = db.execute('select count(distinct pharmacy_qr_barcode(qr_code)) from "qrMovements"').fetchone()[0]
        total = db.execute('select count(*) from barcode').fetchone()[0]
        if not .88 <= used / total <= .92:
            raise AssertionError(f'Visible catalog utilization is {used}/{total}.')
        print(json.dumps({'future_prescriptions_stored':future,'latest_scheduled_date':str(latest),
            'visible_medicines_with_movements':used,'catalog_size':total,'future_rows_visible_to_app':0}),flush=True)
    finally:
        db.execute('RESET ROLE')


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--config', type=Path, default=Path(__file__).resolve().parents[3] / 'config.json')
    parser.add_argument('--maintenance-user', required=True)
    parser.add_argument('--start', type=date.fromisoformat, default=date(2026, 9, 1))
    parser.add_argument('--end', type=date.fromisoformat, default=date(2026, 12, 31))
    parser.add_argument('--dry-run', action='store_true')
    parser.add_argument('--verify-only', action='store_true')
    args = parser.parse_args()
    cfg = json5.loads(args.config.read_text(encoding='utf-8-sig'))['Database']
    if cfg['DatabaseName'] != 'Portfolio':
        raise RuntimeError('Only the dedicated demonstration database may be extended.')
    with psycopg.connect(host=cfg['Hostname'], port=cfg['Port'], dbname=cfg['DatabaseName'],
                         user=args.maintenance_user, password=os.environ['PGPASSWORD'], connect_timeout=8) as db:
        if not db.execute('select rolsuper from pg_roles where rolname=current_user').fetchone()[0]:
            raise RuntimeError('A maintenance administrator is required; the application role cannot import future activity.')
        if not args.verify_only:
            CalendarData(db, args.start, args.end, 20261029).run_calendar()
        verify_calendar(db, args.end)
        print('Calendar generation finished.', flush=True)
        if args.dry_run:
            db.rollback()
            print('Dry run rolled back.', flush=True)
        else:
            print('Committing calendar activity.', flush=True)


if __name__ == '__main__':
    main()
