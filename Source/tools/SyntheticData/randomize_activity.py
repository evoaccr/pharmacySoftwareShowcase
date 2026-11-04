from __future__ import annotations

import argparse
import json
import math
import os
from collections import deque
from datetime import date, datetime, time, timedelta
from decimal import Decimal
from pathlib import Path
from zoneinfo import ZoneInfo

import json5
import psycopg
from psycopg import sql

from generate import Generator, money
from extend_calendar import verify_calendar


class VariedActivity(Generator):
    def __init__(self, db, seed):
        self.first, self.last = date(2026, 9, 1), date(2026, 12, 31)
        super().__init__(db, seed, 122, datetime.combine(self.last, time(23, 59), ZoneInfo('Europe/Istanbul')))
        self.allow_debt = False
        self.staff = ['admin']
        self.inventory = {}
        self.return_candidates = []

    def replace_calendar(self):
        self.db.execute("select pg_advisory_xact_lock(hashtext('synthetic_activity_randomization'))")
        self.db.execute("select pg_advisory_xact_lock(hashtext('refresh_visible_activity'))")
        self.db.execute("select set_config('pharmacy.user_id','admin',true)")
        if self.db.execute("select exists(select 1 from prescription where medula_prescription_no like 'VAR%')").fetchone()[0]:
            raise RuntimeError('Randomized activity already exists; use --verify-only to inspect it.')
        self.target_codes = {r[0] for r in self.db.execute('select distinct pharmacy_qr_barcode(qr_code) from "qrMovements"')}
        self.before = dict(self.db.execute('''select 'sales',count(*) from sales union all select 'movements',count(*) from "qrMovements"'''))
        self.db.execute('''create temporary table old_calendar_codes as select distinct qr_code from "qrMovements" where qr_code like '%(21)SYN1029%' ''')
        self.db.execute('''create temporary table old_calendar_sales as select distinct s.record_id from sales s join "salesQrItems" i on i.sale_id=s.record_id join old_calendar_codes c using(qr_code)''')
        if self.db.execute('select count(*) from old_calendar_sales').fetchone()[0] != 3660:
            raise RuntimeError('The uniform calendar differs from the expected generated layer; refusing to replace it.')
        if self.db.execute('''select exists(select 1 from sales where record_id in(select record_id from old_calendar_sales)
            and (timestamp::time<>time '11:00' or prescription_uid is not null and prescription_uid not in(
                select prescription_uid from prescription where medula_prescription_no like 'CAL%')))''').fetchone()[0]:
            raise RuntimeError('Unrecognized activity uses generated stock; replacement stopped.')
        for table, column in [('salesRefundItems','original_sale_id'),('salesRefunds','original_sale_id'),
            ('prescription_sale_operations','sale_id'),('retail_its_operations','sale_id'),('consignmentSales','settlement_sale_id')]:
            query = sql.SQL('select exists(select 1 from {} where {} in(select record_id from old_calendar_sales))').format(sql.Identifier(table),sql.Identifier(column))
            if self.db.execute(query).fetchone()[0]:
                raise RuntimeError('Additional dependent activity exists; refusing to remove it: '+table)
        self.db.execute('''create temporary table old_calendar_invoices as select distinct line.invoice_uuid from invoice_lines line
            join invoice_line_qr_codes code on code.invoice_line_id=line.record_id join old_calendar_codes old using(qr_code)''')
        self.db.execute('''create temporary table old_calendar_logs as select record_id from invoice_stock_logs where invoice_uuid in(select invoice_uuid from old_calendar_invoices)''')
        if self.db.execute('''select exists(select 1 from invoice_stock_logs where related_log_id in(select record_id from old_calendar_logs)
            and record_id not in(select record_id from old_calendar_logs))''').fetchone()[0]:
            raise RuntimeError('Additional stock history depends on this calendar; replacement stopped.')
        self.db.execute('delete from sales where record_id in(select record_id from old_calendar_sales)')
        self.db.execute("delete from prescription_log where prescription_uid in(select prescription_uid from prescription where medula_prescription_no like 'CAL%')")
        self.db.execute("delete from prescription where medula_prescription_no like 'CAL%'")
        self.db.execute('delete from invoice_stock_log_invoices where invoice_stock_log_id in(select record_id from old_calendar_logs)')
        self.db.execute('delete from invoice_stock_logs where record_id in(select record_id from old_calendar_logs)')
        self.db.execute('delete from invoices where invoice_uuid in(select invoice_uuid from old_calendar_invoices)')
        for table in ['qrActive','qrInactive','qrHanged','qrMovements']:
            self.db.execute(sql.SQL('delete from {} where qr_code in(select qr_code from old_calendar_codes)').format(sql.Identifier(table)))
        for code in self.target_codes:
            if code: self.db.execute('select pharmacy_refresh_barcode_active(%s)',(code,))
        print('Removed only the identified uniform calendar; original activity and card/catalog records retained.',flush=True)

    def prepare(self):
        self.replace_calendar()
        # QR rows remain authoritative during the import. Refresh the derived barcode cache once at the end,
        # instead of rewriting the same aggregate for every inserted/deleted box in this transaction.
        self.stock_refresh_sql = self.db.execute("select pg_get_functiondef('public.pharmacy_refresh_barcode_active(character varying)'::regprocedure)").fetchone()[0]
        self.db.execute('''CREATE OR REPLACE FUNCTION public.pharmacy_refresh_barcode_active(target_barcode character varying)
            RETURNS void LANGUAGE plpgsql AS $$ BEGIN RETURN; END $$''')
        with self.db.cursor(row_factory=psycopg.rows.dict_row) as cur:
            self.medicines = cur.execute('''select b.record_id,b.barcode,b.medicine_name,p.sale_price,p.vat_rate from barcode b
                join "barcodePrices" p on p.record_reference_no=b.record_id where b.barcode=any(%s) order by b.record_id''',(list(self.target_codes),)).fetchall()
            self.doctors = cur.execute('select * from doctor order by record_id').fetchall()
            self.depots = cur.execute('select * from depot order by record_id').fetchall()
            self.patient_rows = {r['record_id']:r for r in cur.execute('select * from patient order by record_id')}
        self.movement_types = dict(self.db.execute('select code,record_id from "qrMovementTypes"'))
        self.diagnoses = [r[0] for r in self.db.execute('select code from icd10_diagnosis order by code')]
        self.invoice_types = [r[0] for r in self.db.execute('select invoice_type_code from prescription_invoice_type where return_status_code=0 order by 1')]
        self.own_address = self.db.execute("select receiver_address_id from invoices where invoice_type='Gelen Fatura' order by database_entry_at limit 1").fetchone()[0]
        addresses = dict(self.db.execute('select gln,record_id from addresses where gln is not null order by record_id'))
        self.depot_addresses = {d['record_id']:addresses[d['gln_code']] for d in self.depots}
        self.rx_rows = [None]*self.db.execute('select count(*) from prescription').fetchone()[0]
        for med in self.medicines:
            med['price'] = money(med['sale_price'])
            med['cost'] = money(med['price']*Decimal('.72'))
            med['demand'] = min(35,max(.035,self.r.lognormvariate(0,1.3)))
            med['season'] = {month:self.r.lognormvariate(0,.6) for month in range(9,13)}
            med['coverage_days'] = self.r.uniform(3,18)
            self.inventory[med['barcode']] = deque()
        self.weight_sum = sum(m['demand'] for m in self.medicines)

    def at(self, day):
        opening = 10 if day.weekday()==6 else 8
        closing = 17 if day.weekday()==6 else 21
        hour = self.r.choices(range(opening,closing),weights=[1+2*math.sin((h-opening+1)/(closing-opening+1)*math.pi) for h in range(opening,closing)])[0]
        return datetime.combine(day,time(hour,self.r.randrange(60),self.r.randrange(60)),self.end.tzinfo)

    def plan(self):
        planned=[]
        day=self.first
        patients=list(self.patient_rows)
        while day<=self.last:
            weekday=[1.08,1.03,1.12,1.02,1.18,.72,.18][day.weekday()]
            monthly={9:.9,10:1.0,11:1.18,12:1.3}[day.month]
            count=max(7,min(580,round(180*weekday*monthly*self.r.lognormvariate(-.06,.42))))
            if self.r.random()<.06:count=max(5,count//4)
            weights=[m['demand']*m['season'][day.month]*self.r.uniform(.75,1.25) for m in self.medicines]
            for _ in range(count):
                chosen={}
                size=self.r.choices([1,2,3,4,5],[.27,.37,.22,.1,.04])[0]
                while len(chosen)<size:
                    med=self.r.choices(self.medicines,weights=weights,k=1)[0]
                    chosen[med['barcode']]=(med,self.r.choices([1,2,3,4],[.69,.23,.065,.015])[0])
                planned.append((self.at(day),self.r.choice(patients),self.r.random()<.67,list(chosen.values())))
            day+=timedelta(days=1)
        # Sparse medicines still receive a few independent, irregular past transactions.
        for med in self.medicines:
            for _ in range(self.r.randint(1,4)):
                day=self.first+timedelta(days=self.r.randrange(58))
                planned.append((self.at(day),self.r.choice(patients),self.r.random()<.7,[(med,self.r.choice([1,1,2,3]))]))
        planned.sort(key=lambda event:event[0])
        print('Planned '+str(len(planned))+' varied sales across 122 days.',flush=True)
        return planned

    def purchase(self, required, quantity, sale_stamp):
        earliest=datetime.combine(self.first,time(7),self.end.tzinfo)
        stamp=max(earliest,sale_stamp-timedelta(hours=self.r.uniform(.3,65),minutes=self.r.randrange(60)))
        if stamp.hour<7:stamp=stamp.replace(hour=self.r.randint(7,9))
        if stamp>=sale_stamp:stamp=sale_stamp-timedelta(minutes=10)
        depot=self.r.choice(self.depots)
        invoice=str(self.uid())
        self.insert('invoices',invoice_uuid=invoice,invoice_type='Gelen Fatura',sender_address_id=self.depot_addresses[depot['record_id']],
            receiver_address_id=self.own_address,issue_date=stamp.date(),due_date=stamp.date()+timedelta(days=self.r.choice([7,14,21,28,35,45])),
            database_entry_at=stamp,total_cost=0,discount_amount=0,kdv_amount=0,payable_amount=0,is_saved=True)
        selected={required['barcode']:required}
        for other in self.r.choices(self.medicines,weights=[m['demand'] for m in self.medicines],k=self.r.choices([0,1,2,3,5],[.2,.3,.26,.18,.06])[0]):
            selected[other['barcode']]=other
        total=Decimal(0)
        for line_number,med in enumerate(selected.values(),1):
            target=180*3*med['demand']/self.weight_sum*med['coverage_days']*self.r.uniform(.65,1.65)
            count=max(quantity if med is required else 1,min(160,round(target)+self.r.randint(2,9)))
            cost=money(med['price']*Decimal(str(self.r.uniform(.65,.81))))
            amount=cost*count;total+=amount
            line=self.insert('invoice_lines',invoice_uuid=invoice,line_number=line_number,barcode=med['barcode'],item_name=med['medicine_name'],
                quantity=count,unit='ADET',unit_price=cost,vat_rate=0,vat_amount=0,discount_amount=0,line_amount=amount,currency='TRY',is_medication=True,
                stock_tracking_type='QR',raw_data={'synthetic':True,'generation':'varied'})
            for _ in range(count):
                box=self.make_qr(dict(med,cost=cost));self.inventory[med['barcode']].append(box)
                self.insert('invoice_line_qr_codes',invoice_line_id=line['record_id'],qr_code=box['qr'],gtin=box['gtin'],barcode=med['barcode'],
                    serial_number=box['serial'],batch_number=box['batch'],expiry_date=box['expiry'],raw_code=box['qr'])
                self.move(box,'DEP_IN',stamp,'INV-'+invoice,depot['gln_code'])
                self.db.execute('update "qrActive" set purchase_cost=%s where qr_code=%s',(cost,box['qr']))
        self.db.execute('update invoices set total_cost=%s,payable_amount=%s where invoice_uuid=%s',(total,total,invoice))
        log=self.insert('invoice_stock_logs',invoice_uuid=invoice,action_code='SAVE',occurred_at=stamp,user_id='admin',reference_code='INV-'+invoice,
            movement_type_id=self.movement_types['DEP_IN'],notes='')
        self.insert('invoice_stock_log_invoices',invoice_stock_log_id=log['record_id'],invoice_uuid=invoice)

    def add_returns(self):
        for original in self.return_candidates:
            stamp=min(original['stamp']+timedelta(days=self.r.randint(1,12),hours=self.r.randrange(5)),self.end-timedelta(minutes=1))
            if stamp<=original['stamp']:continue
            item=self.r.choice(original['items']);box=item['box']
            refund=self.insert('salesRefunds',patient_id=original['patient'],total_amount=item['net'],patient_amount=item['net'],user_no='admin',
                timestamp=self.stamp(stamp),discount_amount=item['discount'],original_sale_id=original['row']['record_id'],refund_source='retail',
                refund_status='completed',notes='',historical_import=False)
            self.insert('salesRefundItems',refund_id=refund['record_id'],original_sale_id=original['row']['record_id'],qr_code=box['qr'],barcode=box['med']['barcode'],
                gross_price=item['gross'],discount_amount=item['discount'],exit_price=item['net'],entry_cost=box['med']['cost'],item_source='retail',inventory_tracked=True)
            self.insert('salesRefundPayements',refund_id=refund['record_id'],payment_type=self.r.choice([1,2,3]),amount=item['net'],payment_role='patient',payment_finished=True,settled_at=stamp)
            self.move(box,'PERAK_RETURN',stamp,f'G{refund["record_id"]}',self.patient_rows[original['patient']]['tc_identity_no'])

    def run_varied(self):
        self.prepare()
        for index,(stamp,patient,is_rx,lines) in enumerate(self.plan(),1):
            boxes=[]
            for med,quantity in lines:
                if len(self.inventory[med['barcode']])<quantity:self.purchase(med,quantity,stamp)
                boxes.extend(self.inventory[med['barcode']].popleft() for _ in range(quantity))
            record=self.sale(stamp,patient,is_rx,boxes=boxes,prescription_prefix='VAR')
            if not is_rx and self.r.random()<.045 and stamp<self.end-timedelta(days=1):self.return_candidates.append(record)
            if index%2000==0:print(f'Created {index} transactions through {stamp.date()}.',flush=True)
        self.add_returns()
        self.db.execute(self.stock_refresh_sql)
        for med in self.medicines:self.db.execute('select pharmacy_refresh_barcode_active(%s)',(med['barcode'],))
        self.db.execute('''update loyal_patient_report_supply_evidence e set total_report_count=(select count(*) from prescription p
            join patient_identity i on i.tc_identity_no=p.patient_tc_identity_no where i.patient_record_id=e.patient_record_id and not p.is_deleted and p.saved_at<=e.evaluated_at)''')
        self.db.execute('select public.refresh_visible_activity()')
        verify_calendar(self.db)
        verify_variation(self.db)


def verify_variation(db):
    count=db.execute("select count(*) from prescription where medula_prescription_no like 'VAR%'").fetchone()[0]
    if count<9000:raise AssertionError('Insufficient transaction volume.')
    bad=db.execute('''select count(*) from sales s where total_amount<>(select coalesce(sum(amount),0) from "salesPayements" p where p.sale_id=s.record_id)''').fetchone()[0]
    if bad:raise AssertionError('Payment totals do not match sales.')
    bad=db.execute('''select count(*) from invoices i where total_cost<>(select coalesce(sum(line_amount),0) from invoice_lines l where l.invoice_uuid=i.invoice_uuid)''').fetchone()[0]
    if bad:raise AssertionError('Invoice totals do not match lines.')
    bad=db.execute('''select count(*) from prescription p where medula_prescription_no like 'VAR%' and (
        cardinality(qr_codes)<>(select sum(p.final_content[n][3]::integer) from generate_subscripts(p.final_content,1) n)
        or prescription_total<>(select sum(p.final_content[n][8]::numeric) from generate_subscripts(p.final_content,1) n))''').fetchone()[0]
    if bad:raise AssertionError('Prescription quantities or line amounts do not match their QR items.')
    bad=db.execute('''select count(*) from "salesRefunds" r where total_amount<>(select coalesce(sum(exit_price),0) from "salesRefundItems" i where i.refund_id=r.record_id)
        or total_amount<>(select coalesce(sum(amount),0) from "salesRefundPayements" p where p.refund_id=r.record_id)''').fetchone()[0]
    if bad:raise AssertionError('Refund totals do not match items/payments.')
    db.execute('SET LOCAL ROLE portfolio')
    try:
        stats=db.execute('''select min(n),percentile_cont(.5) within group(order by n),max(n),stddev_pop(n)/avg(n),count(distinct n)
            from(select pharmacy_qr_barcode(qr_code) b,count(*) n from "qrMovements"
                where pharmacy_qr_barcode(qr_code) is not null group by 1) x''').fetchone()
        if stats[3]<1 or stats[4]<100:raise AssertionError('Medicine demand is still too uniform.')
        hours=db.execute("select count(distinct extract(hour from timestamp)),avg((extract(hour from timestamp)<>11)::int) from sales where record_id in(select sale_id from \"salesQrItems\" where qr_code like '%(21)SYN2901%')").fetchone()
        if hours[0]<10 or hours[1]<.85:raise AssertionError('Sales are not sufficiently spread through the day.')
        daily=db.execute('''select min(n),max(n),stddev_pop(n)/avg(n) from(select timestamp::date,count(*) n from sales group by 1) x''').fetchone()
        if daily[2]<.3:raise AssertionError('Daily transaction traffic is too even.')
        profiles=db.execute('''with daily as(select pharmacy_qr_barcode(qr_code) barcode,movement_date::date activity_day,count(*) n
            from "qrMovements" where pharmacy_qr_barcode(qr_code) is not null group by 1,2), profiles as(select barcode,md5(string_agg(activity_day::text||':'||n::text,',' order by activity_day)) profile
            from daily group by barcode) select count(distinct profile),count(*) from profiles''').fetchone()
        if profiles[0]<profiles[1]*.98:raise AssertionError('Too many medicines share the same daily movement profile.')
        print(json.dumps({'visible_movement_count_min_median_max':[float(v) for v in stats[:3]],'movement_volume_cv':float(stats[3]),
            'distinct_medicine_volumes':stats[4],'unique_daily_medicine_profiles':profiles[0],'transaction_hours':hours[0],
            'daily_transactions_min_max':[daily[0],daily[1]],'daily_traffic_cv':float(daily[2])}),flush=True)
    finally:
        if db.info.transaction_status != psycopg.pq.TransactionStatus.INERROR: db.execute('RESET ROLE')


def main():
    parser=argparse.ArgumentParser()
    parser.add_argument('--config',type=Path,default=Path(__file__).resolve().parents[3]/'config.json')
    parser.add_argument('--maintenance-user',required=True)
    parser.add_argument('--dry-run',action='store_true')
    parser.add_argument('--verify-only',action='store_true')
    args=parser.parse_args()
    cfg=json5.loads(args.config.read_text(encoding='utf-8-sig'))['Database']
    if cfg['DatabaseName']!='Portfolio':raise RuntimeError('Only the dedicated demonstration database can be changed.')
    with psycopg.connect(host=cfg['Hostname'],port=cfg['Port'],dbname=cfg['DatabaseName'],user=args.maintenance_user,password=os.environ['PGPASSWORD']) as db:
        if not db.execute('select rolsuper from pg_roles where rolname=current_user').fetchone()[0]:raise RuntimeError('Maintenance administrator required.')
        if args.verify_only:verify_calendar(db);verify_variation(db)
        else:VariedActivity(db,2026102901).run_varied()
        if args.dry_run:db.rollback();print('Dry run passed and rolled back.',flush=True)
        else:print('Validation passed; committing.',flush=True)


if __name__=='__main__':main()
