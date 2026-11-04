from __future__ import annotations

import argparse
import json
import random
import uuid
from collections import Counter, defaultdict
from datetime import date, datetime, time, timedelta, timezone
from decimal import Decimal
from pathlib import Path
from zoneinfo import ZoneInfo

import json5
import psycopg
from psycopg import sql
from psycopg.types.json import Jsonb


FIRST = 'Ada Alice Amelia Arlo Arthur Ava Benjamin Caleb Chloe Clara Daniel Daisy Dylan Elena Elias Emily Ethan Felix Finn Freya Grace Hannah Harper Henry Hugo Iris Isaac Isla Ivy Jack Jacob James Jasper Jonah Joseph Julia Leo Liam Lily Logan Luca Lucy Luna Maya Mia Milo Naomi Nathan Nora Oliver Oscar Owen Poppy Quinn Riley Robin Rose Ruby Samuel Sasha Scarlett Sebastian Sofia Theo Thomas Violet Willow Zara'.split()
LAST = 'Alder Ashford Bell Brook Calder Carver Clay Cooper Dale Ellis Emerson Finch Fletcher Ford Fox Gray Green Hale Hart Hawthorne Hayes Hill Hollis Hudson Lake Lane Linden Marsh Mason Meadow Miller Morgan Oak Parker Reed Rivers Rowan Sawyer Shaw Stone Summer Taylor Vale Walker Wells West Wilder Winter Wood York'.split()
PLACES = 'Alderfield Brookhaven Cedarvale Fairmeadow Glenford Lakeside Maplehurst Northmere Oakridge Pinebrook Riverbend Silverton Westhaven Willowdale'.split()
CENT = Decimal('.01')


def money(value):
    return Decimal(str(value)).quantize(CENT)


def plain(value):
    if isinstance(value, (datetime, date, uuid.UUID, Decimal)):
        return str(value)
    if isinstance(value, dict):
        return {k: plain(v) for k, v in value.items()}
    if isinstance(value, (tuple, list)):
        return [plain(v) for v in value]
    return value


class Generator:
    def __init__(self, connection, seed, days, end):
        self.db = connection
        self.r = random.Random(seed)
        self.seed = seed
        self.days = days
        self.end = end
        self.start = datetime.combine(end.date() - timedelta(days=days - 1), time(8), end.tzinfo)
        self.profile = json.loads(Path(__file__).with_name('reference-profile.json').read_text())
        self.stock = defaultdict(list)
        self.serial = 0
        self.events = []
        self.refunds = []
        self.debts = Counter()
        self.patient_rows = {}
        self.rx_rows = []
        self.sale_rows = []
        self.invoice_rows = []
        self.audit_tables = ['patient_log', 'doctor_log', 'depot_log', 'faculty_log', 'family_group_log', 'pharmacy_log', 'users_log', 'kurum_log']

    def uid(self):
        return uuid.UUID(int=self.r.getrandbits(128), version=4)

    def insert(self, table, **values):
        values = {k: Jsonb(plain(v)) if isinstance(v, dict) else v for k, v in values.items()}
        query = sql.SQL('INSERT INTO {} ({}) VALUES ({}) RETURNING *').format(
            sql.Identifier('public', table), sql.SQL(',').join(map(sql.Identifier, values)),
            sql.SQL(',').join(sql.Placeholder() for _ in values))
        with self.db.cursor(row_factory=psycopg.rows.dict_row) as cur:
            return cur.execute(query, list(values.values())).fetchone()

    def optional(self, table, **values):
        return {k: (None if self.r.random() < self.profile.get(table, {}).get(k, .2) else v)
                for k, v in values.items()}

    def name(self):
        return self.r.choice(FIRST), self.r.choice(LAST)

    def when(self, first=4, last=None):
        last = self.days - 1 if last is None else last
        day = self.start.date() + timedelta(days=self.r.randint(first, last))
        value = datetime.combine(day, time(self.r.randint(9, 18), self.r.randrange(60)), self.end.tzinfo)
        return min(value, self.end - timedelta(minutes=2))

    def stamp(self, stamp):
        return stamp.replace(tzinfo=None)

    def gln(self, i):
        return f'999{i:010d}'

    def contact(self, table, i):
        # Reserved domains and deliberately non-dialable phone strings.
        result = self.optional(table, address=f'{i} Lane', city=self.r.choice(PLACES),
                             district='District', phone_numbers=[f'000-{i:07d}'],
                             email_addresses=[f'contact{i}@demo.invalid'])
        if table == 'patient':
            result.pop('city'); result.pop('district')
        return result

    def bootstrap(self):
        database, user = self.db.execute('select current_database(),current_user').fetchone()
        if database != 'Portfolio' or user != 'portfolio':
            raise RuntimeError('Generation is restricted to the dedicated Portfolio database/login.')
        self.db.execute("select pg_advisory_xact_lock(hashtext('synthetic_data_generator'))")
        for table in ['patient', 'doctor', 'depot', 'pharmacy', 'prescription', 'sales', 'invoices', 'qrMovements']:
            if self.db.execute(sql.SQL('select count(*) from {}').format(sql.Identifier(table))).fetchone()[0]:
                raise RuntimeError(f'{table} already contains data; refusing to overwrite it. Use --verify-only instead.')
        self.db.execute("select set_config('pharmacy.user_id','admin',true)")
        self.db.execute('DELETE FROM faculty')
        self.db.execute('TRUNCATE faculty_log')
        self.reset_sequences()
        self.branches = [r[0] for r in self.db.execute('select branch_id from branches order by branch_id')]
        self.faculty_types = [r[0] for r in self.db.execute('select faculty_type_id from faculty_type order by 1')]
        self.institutions = [r[0] for r in self.db.execute('select record_id from kurum order by 1')]
        self.invoice_types = [r[0] for r in self.db.execute('select invoice_type_code from prescription_invoice_type where return_status_code=0 order by 1')]
        self.diagnoses = [r[0] for r in self.db.execute('select code from icd10_diagnosis order by code')]
        self.movement_types = dict(self.db.execute('select code,record_id from "qrMovementTypes"'))
        self.medicines = []
        with self.db.cursor(row_factory=psycopg.rows.dict_row) as cur:
            available = cur.execute('''select b.record_id,b.barcode,b.medicine_name,p.sale_price,p.vat_rate
                from barcode b join "barcodePrices" p on p.record_reference_no=b.record_id
                where b.barcode ~ '^[0-9]{13}$' and p.sale_price>0 order by b.record_id''').fetchall()
        for row in self.r.sample(available, 300):
            row['price'] = money(row['sale_price'])
            row['cost'] = money(row['price'] * Decimal('.72'))
            self.medicines.append(row)
        self.med_by_barcode = {m['barcode']: m for m in self.medicines}
        self.faculties, self.doctors, self.depots, self.pharmacies = [], [], [], []
        self.staff = ['admin']
        self.db.execute("INSERT INTO users(user_id,first_name,last_name,password,permisson_level,status,session_active) VALUES ('admin','Administrator','',%s,'A',true,false) ON CONFLICT(user_id) DO UPDATE SET password=excluded.password,permisson_level='A',status=true",('portfoli123',))
        for i in range(1, 101):
            faculty = self.insert('faculty', faculty_code=f'SYNFAC{i:04}', faculty_name=f'{self.r.choice(PLACES)} Medical Centre {i}',
                faculty_type_id=self.r.choice(self.faculty_types), country='Northland',
                ownership_type=self.r.choice(['Kamu', 'Özel']), **self.contact('faculty', i))
            self.faculties.append(faculty)
            first, last = self.name()
            self.doctors.append(self.insert('doctor', diploma_registration_no=f'SYNDR{i:06}',
                diploma_no=f'SYNDIP{i:06}', first_name=first, last_name=last, facility_no=faculty['faculty_code'],
                branch_id=self.r.choice(self.branches), **self.optional('doctor', phone_numbers=[f'000-{i:07}'],
                email_addresses=[f'doctor{i}@demo.invalid'], certificate=f'CERT-{i:04}')))
            self.depots.append(self.insert('depot', gln_code=self.gln(i), depot_name=f'{self.r.choice(PLACES)} Depot {i}',
                is_active=True, debt_balance=0, contact_person=' '.join(self.name()),
                business_phone_numbers=[f'000-{i:07}'], email_addresses=[f'depot{i}@demo.invalid'],
                address=f'{i} Industrial Way', city=self.r.choice(PLACES), web_username=None, web_password=None))
            self.pharmacies.append(self.insert('pharmacy', gln_code=self.gln(1000+i),
                pharmacy_name=f'{self.r.choice(PLACES)} Pharmacy {i}', pharmacist_name=' '.join(self.name()),
                debt_balance=0, **self.contact('pharmacy', 1000+i)))
        for institution in self.institutions:
            # Retain directly imported institutional lookup data; record its refresh.
            self.db.execute('update kurum set kurum_name=kurum_name where record_id=%s',(institution,))
        self.own_gln = self.gln(9000)
        self.own_address = self.insert('addresses', party_name='Pharmacy', identity_no='0000000000', gln=self.own_gln)['record_id']
        self.depot_addresses = {}
        for depot in self.depots:
            self.depot_addresses[depot['record_id']] = self.insert('addresses', party_name=depot['depot_name'],
                identity_no=f'000{depot["record_id"]:07}', gln=depot['gln_code'])['record_id']
        self.pharmacy_addresses = {}
        for pharmacy in self.pharmacies:
            self.pharmacy_addresses[pharmacy['record_id']] = self.insert('addresses', party_name=pharmacy['pharmacy_name'],
                identity_no=f'001{pharmacy["record_id"]:07}', gln=pharmacy['gln_code'])['record_id']
        for i in range(1, 1001):
            first, last = self.name()
            doctor = self.r.choice(self.doctors)
            birth = date(self.r.randint(1945, 2007), self.r.randint(1, 12), self.r.randint(1, 28))
            # Starts with zero, so it cannot be a genuine Turkish national identity number.
            identity = f'0000{i:07}'
            patient = self.insert('patient', tc_identity_no=identity, first_name=first, last_name=last,
                date_of_birth=birth, gender=self.r.choice(['Kadın', 'Erkek']), family_doctor_id=doctor['diploma_registration_no'],
                kurum_id=self.r.choice(self.institutions), debt_amount=0, allow_debt=True, maximum_debt=100000,
                alive=True, patient_type=self.r.choice(['1', '2']), note='',
                **self.contact('patient', i))
            self.patient_rows[patient['record_id']] = patient
            self.insert('patient_identity', tc_identity_no=identity, patient_record_id=patient['record_id'])
        patients = list(self.patient_rows)
        self.r.shuffle(patients)
        for i in range(100):
            members = patients[i*4:(i+1)*4]
            group = self.insert('family_group', family_group_name=f'{self.r.choice(LAST)} Family {i+1}',
                main_patient_id=members[0], member_ids=members)
            self.db.execute('update patient set family_group_id=%s where record_id=any(%s)', (group['record_id'], members))
        for med in self.r.sample(self.medicines, 100):
            self.insert('barcodeAnalysis', record_reference_no=med['record_id'], wanted_stock_months=self.r.choice([.5,1,1.5,2,2.5,3]), updated_at=self.stamp(self.when()))
            self.insert('barcodeCampaign', record_reference_no=med['record_id'], warehouse_gln=self.r.choice(self.depots)['gln_code'],
                purchase_quantity=self.r.choice([5,10,20]), bonus_quantity=self.r.choice([1,2,3]), period_days=30,
                start_date=self.start.date(), end_date=self.end.date()+timedelta(days=10))
            for d, factor in [(self.start.date(), Decimal('.97')), (self.end.date(), Decimal(1))]:
                self.insert('barcodePriceHistory', record_reference_no=med['record_id'], effective_date=d,
                    sale_price=money(med['price']*factor), warehouse_price=money(med['cost']*factor),
                    public_price=money(med['price']*factor), vat_rate=med['vat_rate'])
        print('Created 1000 patients and 100 doctors, depots, pharmacies and facilities; admin is the sole user.', flush=True)

    def make_qr(self, med):
        self.serial += 1
        expiry = self.end.date()+timedelta(days=self.r.randint(90, 720))
        serial = f'SYN{self.seed%10000:04}{self.serial:09}'
        batch = f'SYN{self.serial//100:05}'
        gtin = med['barcode'].zfill(14)
        qr = f'(01){gtin}(21){serial}(17){expiry:%y%m%d}(10){batch}'
        return dict(qr=qr, med=med, expiry=expiry, gtin=gtin, serial=serial, batch=batch)

    def move(self, box, code, stamp, reference, address=None):
        self.insert('qrMovements', qr_code=box['qr'], movement_date=self.stamp(stamp), movement_address=address,
                    user_id=self.r.choice(self.staff), reference_code=reference,
                    movement_type=self.movement_types[code], notes='')

    def buy_stock(self):
        for i in range(100):
            depot = self.depots[i]
            stamp = self.start+timedelta(days=i%3, minutes=i)
            invoice_id = str(self.uid())
            self.insert('invoices', invoice_uuid=invoice_id, invoice_type='Gelen Fatura',
                sender_address_id=self.depot_addresses[depot['record_id']], receiver_address_id=self.own_address,
                issue_date=stamp.date(), due_date=stamp.date()+timedelta(days=30), database_entry_at=stamp,
                total_cost=0, discount_amount=0, kdv_amount=0, payable_amount=0, is_saved=True)
            total = Decimal(0)
            for j, med in enumerate(self.medicines[i*3:(i+1)*3], 1):
                count = self.r.randint(38, 48)
                amount = money(med['cost']*count); total += amount
                line = self.insert('invoice_lines', invoice_uuid=invoice_id, line_number=j, barcode=med['barcode'],
                    item_name=med['medicine_name'], quantity=count, unit='ADET', unit_price=med['cost'], vat_rate=0,
                    vat_amount=0, discount_amount=0, line_amount=amount, currency='TRY', is_medication=True,
                    stock_tracking_type='QR', raw_data={'synthetic':True, 'quantity':count})
                for _ in range(count):
                    box = self.make_qr(med)
                    self.insert('invoice_line_qr_codes', invoice_line_id=line['record_id'], qr_code=box['qr'], gtin=box['gtin'],
                        barcode=med['barcode'], serial_number=box['serial'], batch_number=box['batch'],
                        expiry_date=box['expiry'], raw_code=box['qr'])
                    self.move(box, 'DEP_IN', stamp, f'INV-{invoice_id}', depot['gln_code'])
                    self.db.execute('update "qrActive" set purchase_cost=%s where qr_code=%s',(med['cost'],box['qr']))
                    self.stock[med['barcode']].append(box)
            self.db.execute('update invoices set total_cost=%s,payable_amount=%s where invoice_uuid=%s',(total,total,invoice_id))
            log = self.insert('invoice_stock_logs', invoice_uuid=invoice_id, action_code='SAVE', occurred_at=stamp,
                user_id='admin', reference_code=f'INV-{invoice_id}', movement_type_id=self.movement_types['DEP_IN'], notes='')
            self.insert('invoice_stock_log_invoices', invoice_stock_log_id=log['record_id'], invoice_uuid=invoice_id)
            self.db.execute('update depot set debt_balance=debt_balance+%s where record_id=%s',(total,depot['record_id']))
            self.invoice_rows.append(dict(id=invoice_id,stamp=stamp,depot=depot,total=total))
        print('Created 100 purchase invoices and opening QR stock through stock triggers.', flush=True)

    def take(self):
        available = [barcode for barcode, boxes in self.stock.items() if boxes]
        if not available:
            raise RuntimeError('Generated demand exceeded purchased stock.')
        return self.stock[self.r.choice(available)].pop()

    def schedule(self):
        patients = list(self.patient_rows)
        for patient in patients:
            for _ in range(self.r.randint(1, 3)):
                self.events.append((self.when(), 'prescription', patient))
        for _ in range(1000):
            self.events.append((self.when(), 'retail', self.r.choice(patients)))
        for kind in ['consignment','trade','manual']:
            for _ in range(100):
                self.events.append((self.when(),kind,self.r.choice(patients)))
        self.events.sort(key=lambda x:x[0])

    def sale(self, stamp, patient_id, prescription, boxes=None, prescription_prefix='SYN'):
        patient = self.patient_rows[patient_id]
        boxes = boxes if boxes is not None else [self.take() for _ in range(self.r.randint(1, 3))]
        items = []
        discounts = {}
        for box in boxes:
            gross = box['med']['price']
            if box['med']['barcode'] not in discounts:
                discounts[box['med']['barcode']] = self.r.choice([Decimal(0),Decimal('.05'),Decimal('.1')])
            discount = money(gross * discounts[box['med']['barcode']])
            items.append(dict(box=box,gross=gross,discount=discount,net=gross-discount))
        total = sum((x['net'] for x in items), Decimal(0))
        institution = money(total*Decimal('.8')) if prescription else Decimal(0)
        patient_due = total-institution
        debt = money(patient_due*Decimal('.4')) if not prescription and getattr(self, "allow_debt", True) and self.r.random()<.15 else Decimal(0)
        paid = patient_due-debt
        uid = self.uid() if prescription else None
        number = f'{prescription_prefix}{len(self.rx_rows)+1:07}' if prescription else None
        user = self.r.choice(self.staff)
        rx = None
        if prescription:
            doctor = self.r.choice(self.doctors)
            groups = defaultdict(list)
            for item in items: groups[item['box']['med']['barcode']].append(item)
            matrix = [[barcode, group[0]['box']['med']['medicine_name'], str(len(group)), '1','Day','1','1',
                       str(sum((item['net'] for item in group), Decimal(0))), '0',str(len(group)),'',
                       (stamp.date()+timedelta(days=30)).isoformat(), '', '', ''] for barcode,group in groups.items()]
            rx = self.insert('prescription', prescription_uid=uid, version_number=1, medula_prescription_no=number,
                e_prescription_no=f'SE{len(self.rx_rows)+1:05}', protocol_no=f'SYNPROTO{len(self.rx_rows)+1:07}',
                facility_code=doctor['facility_no'], doctor_diploma_registration_no=doctor['diploma_registration_no'],
                doctor_branch_id=doctor['branch_id'], patient_tc_identity_no=patient['tc_identity_no'],
                recipient_tc_identity_no=patient['tc_identity_no'], recipient_relationship='0',
                prescription_date=stamp.date(), medication_purchase_date=stamp.date(), medula_filter_date=stamp.date(),
                has_qr_codes=True, is_sequential_distribution=False, registered_content=matrix,final_content=matrix,
                icd10_diagnoses=self.r.sample(self.diagnoses,self.r.randint(1,2)),
                prescription_note='', patient_type='Normal',
                patient_insurance_type=self.r.choice(['Emekli','Çalışan']), patient_coverage_type='4A',
                institution_total_payment=institution, patient_total_payment=patient_due,prescription_total=total,
                prescription_copayment_cash=0,prescription_copayment_salary=0,examination_copayment_cash=0,
                examination_copayment_salary=0,medication_copayment=patient_due,equivalent_price_difference=0,
                qr_codes=[b['qr'] for b in boxes],saved_by_user_id=user,saved_at=stamp,
                imported_from_medula=False,imported_from_farmassist=False,is_deleted=False,list_status_code='K',
                invoice_type_code=self.r.choice(self.invoice_types),qr_status_code=2,prescription_type_code=1,
                prescription_subtype_code=1,provision_type_code=1,identity_fingerprint=f'SYNTHETIC-{uid}')
            self.insert('prescription_log',prescription_uid=uid,version_number=1,action_type='INSERT',
                        update_datetime=stamp,user_id=user,prescription_snapshot=plain(rx))
            self.rx_rows.append(rx)
        sale = self.insert('sales', patient_id=patient_id,total_amount=total,patient_amount=patient_due,
            institution_amount=institution,owed_amount=debt,debted=debt>0,user_no=user,timestamp=self.stamp(stamp),
            prescription_uid=uid,prescription_no=number,sale_status='completed',with_iskonto=any(x['discount']>0 for x in items),
            prescription_finalized_at=stamp if prescription else None,notes='')
        reference = f'P{sale["record_id"]}'
        for item in items:
            box=item['box'];self.move(box,'PRESC_OUT' if prescription else 'PERAK_OUT',stamp,reference,patient['tc_identity_no'])
            self.db.execute('update "qrInactive" set exit_price=%s where qr_code=%s',(item['net'],box['qr']))
            self.insert('salesQrItems',sale_id=sale['record_id'],qr_code=box['qr'],barcode=box['med']['barcode'],
                gross_price=item['gross'],discount_amount=item['discount'],exit_price=item['net'],
                entry_cost=box['med']['cost'],item_source='prescription' if prescription else 'retail',inventory_tracked=True)
        if paid>0:
            kind=self.r.choice([1,2,3])
            self.insert('salesPayements',sale_id=sale['record_id'],payment_type=kind,amount=paid,
                payment_role={1:'patient_cash',2:'patient_credit',3:'patient_bank_transfer'}[kind],payment_finished=True,settled_at=stamp,owed_amount=0)
        if debt>0:
            self.insert('salesPayements',sale_id=sale['record_id'],payment_type=4,amount=debt,payment_role='patient_debt',
                payment_finished=False,owed_amount=debt)
            self.debts[patient_id]+=debt
            self.db.execute("select set_config('pharmacy.reference_code',%s,true)",(reference,))
            self.db.execute('update patient set debt_amount=debt_amount+%s where record_id=%s',(debt,patient_id))
            self.db.execute('update patient_debt_log set timestamp=%s,user_id=%s where patient_record_id=%s and reference_code=%s',(stamp,user,patient_id,reference))
        if institution>0:self.insert('salesPayements',sale_id=sale['record_id'],payment_type=5,amount=institution,
            payment_role='institution_primary',payment_finished=True,settled_at=stamp,owed_amount=0)
        record=dict(row=sale,items=items,stamp=stamp,patient=patient_id,rx=rx,debt=debt)
        self.sale_rows.append(record)
        if not prescription and debt==0 and len(self.refunds)<100 and stamp<self.end-timedelta(days=2):
            self.refunds.append(record)
        return record

    def consignment(self, stamp, patient):
        box=self.take(); price=box['med']['price'];user=self.r.choice(self.staff)
        sale=self.insert('consignmentSales',patient_id=patient,total_amount=price,paid_amount=price,debt_amount=0,
            status='reserved',user_no=user,timestamp=self.stamp(stamp),notes='')
        self.insert('consignmentSaleItems',consignment_sale_id=sale['record_id'],qr_code=box['qr'],barcode=box['med']['barcode'],
                    gross_price=price,discount_amount=0,net_price=price,entry_cost=box['med']['cost'])
        self.insert('consignmentSalePayements',consignment_sale_id=sale['record_id'],payment_type=1,amount=price,settlement_status='reserved')
        self.move(box,'RESERVED',stamp,f'E{sale["record_id"]}',self.patient_rows[patient]['tc_identity_no'])
        self.db.execute('select pharmacy_refresh_consignment_quantity(%s)',(box['med']['barcode'],))
        self.db.execute('update patient set consigned_qr_codes=array_append(coalesce(consigned_qr_codes,ARRAY[]::varchar[]),%s) where record_id=%s',(box['qr'],patient))

    def trade(self, stamp, _):
        box=self.take();med=box['med'];partner=self.r.choice(self.pharmacies);transit=self.r.random()<.35
        trade=self.insert('takas',pharmacy_id=partner['record_id'],direction='outgoing',source_gln=self.own_gln,
            destination_gln=partner['gln_code'],status='partially_accepted' if transit else 'accepted',debt_amount=med['cost'],
            user_no=self.r.choice(self.staff),created_at=self.stamp(stamp),updated_at=self.stamp(stamp),
            transferred_at=self.stamp(stamp),accepted_at=None if transit else self.stamp(stamp),
            debt_applied_at=self.stamp(stamp),next_status_check_at=None,
            notes='')
        item=self.insert('takasQrItems',takas_id=trade['record_id'],qr_code=box['qr'],barcode=med['barcode'],gtin=box['gtin'],
            serial_number=box['serial'],batch_number=box['batch'],expiry_date=box['expiry'],entry_cost=med['cost'],
            debt_amount=med['cost'],status='pending_transfer',status_changed_at=self.stamp(stamp))
        self.move(box,'TRANSF_OUT',stamp,f'T{trade["record_id"]}',partner['gln_code'])
        self.db.execute('update "qrHanged" set hanged_at=%s where qr_code=%s',(self.stamp(stamp),box['qr']))
        self.db.execute('update "takasQrItems" set status=%s where record_id=%s',('in_transit' if transit else 'accepted',item['record_id']))
        if not transit:
            self.db.execute('delete from "qrHanged" where qr_code=%s',(box['qr'],))
            self.insert('qrInactive',qr_code=box['qr'],barcode=med['barcode'],expiry_date=box['expiry'],
                entry_cost=med['cost'],exit_price=med['cost'],reference_no=f'T{trade["record_id"]}')
            self.db.execute('update pharmacy set debt_balance=debt_balance+%s where record_id=%s',(med['cost'],partner['record_id']))
        # A barcode-only item is a distinct extra physical unit, not a duplicate of the QR item.
        self.move(dict(qr=med['barcode']),'MAN_IN',stamp,f'TB-IN-{trade["record_id"]}')
        self.move(dict(qr=med['barcode']),'MAN_OUT',stamp,f'TB-OUT-{trade["record_id"]}')
        self.insert('takasBarcodeItems',takas_id=trade['record_id'],barcode=med['barcode'],entry_cost=med['cost'],debt_amount=med['cost'],status='accepted')
        self.db.execute('update takas set debt_amount=debt_amount+%s where record_id=%s',(med['cost'],trade['record_id']))
        self.db.execute('update pharmacy set debt_balance=debt_balance+%s where record_id=%s',(med['cost'],partner['record_id']))
        invoice_id=str(self.uid())
        self.insert('invoices',invoice_uuid=invoice_id,invoice_type='Giden Fatura',sender_address_id=self.own_address,
            receiver_address_id=self.pharmacy_addresses[partner['record_id']],issue_date=stamp.date(),
            due_date=stamp.date()+timedelta(days=30),database_entry_at=stamp,total_cost=2*med['cost'],
            discount_amount=0,kdv_amount=0,payable_amount=2*med['cost'],is_saved=True)
        for line_number,tracking in [(1,'QR'),(2,'BARCODE')]:
            line=self.insert('invoice_lines',invoice_uuid=invoice_id,line_number=line_number,barcode=med['barcode'],
                item_name=med['medicine_name'],quantity=1,unit='ADET',unit_price=med['cost'],vat_rate=0,vat_amount=0,
                discount_amount=0,line_amount=med['cost'],currency='TRY',is_medication=True,stock_tracking_type=tracking,
                raw_data={'synthetic':True,'trade_id':trade['record_id']})
            if tracking=='QR':
                self.insert('invoice_line_qr_codes',invoice_line_id=line['record_id'],qr_code=box['qr'],gtin=box['gtin'],barcode=med['barcode'],
                    serial_number=box['serial'],batch_number=box['batch'],expiry_date=box['expiry'],raw_code=box['qr'])
        log=self.insert('invoice_stock_logs',invoice_uuid=invoice_id,action_code='SAVE',occurred_at=stamp,user_id='admin',
            reference_code=f'T{trade["record_id"]}',movement_type_id=self.movement_types['TRANSF_OUT'],notes='')
        self.insert('invoice_stock_log_invoices',invoice_stock_log_id=log['record_id'],invoice_uuid=invoice_id)

    def manual(self, stamp, _):
        box=self.take(); user=self.r.choice(self.staff)
        entry=self.insert('manual_movements',movement_direction='OUT',its_notification_enabled=False,user_id=user,
            timestamp=self.stamp(stamp),note='')
        reference=f'M{entry["record_id"]}';self.move(box,'MAN_OUT',stamp,reference)
        self.insert('unnotified_its_movements',qr_code=box['qr'],reference_no=reference,movement_direction='OUT',
            notification_type='SALE',is_corrected=False,created_by=user,created_at=stamp)
        operation=self.uid()
        self.insert('stock_its_operations',operation_id=operation,operation_type='SALE',operation_status='failed',
            pharmacy_gln=self.own_gln,request_payload={'synthetic':True,'network_sent':False},qr_codes=[box['qr']],
            requested_by=user,source_reference=reference,prepared_at=stamp,error_text='SYNTHETIC_OFFLINE: never submitted',manual_movement_id=entry['record_id'])
        self.insert('stock_its_operation_items',operation_id=operation,qr_code=box['qr'],is_active=False,result_code='SYNTHETIC_OFFLINE')

    def finish_sales(self):
        for original in self.refunds:
            item=original['items'][0];box=item['box'];stamp=min(original['stamp']+timedelta(days=self.r.randint(1,2)),self.end-timedelta(minutes=1))
            refund=self.insert('salesRefunds',patient_id=original['patient'],total_amount=item['net'],patient_amount=item['net'],
                user_no='admin',timestamp=self.stamp(stamp),discount_amount=item['discount'],original_sale_id=original['row']['record_id'],
                refund_source='retail',refund_status='completed',notes='',historical_import=False)
            self.insert('salesRefundItems',refund_id=refund['record_id'],original_sale_id=original['row']['record_id'],qr_code=box['qr'],
                barcode=box['med']['barcode'],gross_price=item['gross'],discount_amount=item['discount'],exit_price=item['net'],
                entry_cost=box['med']['cost'],item_source='retail',inventory_tracked=True)
            self.insert('salesRefundPayements',refund_id=refund['record_id'],payment_type=1,amount=item['net'],payment_role='patient',payment_finished=True,settled_at=stamp)
            self.move(box,'PERAK_RETURN',stamp,f'R{refund["record_id"]}',self.patient_rows[original['patient']]['tc_identity_no'])
        for index,record in enumerate(self.sale_rows[:100]):
            operation=self.uid(); qr=[x['box']['qr'] for x in record['items']]
            self.insert('retail_its_operations',operation_id=operation,operation_type='SALE',operation_status='failed',pharmacy_gln=self.own_gln,
                request_payload={'synthetic':True,'network_sent':False},qr_codes=qr,requested_by='admin',prepared_at=record['stamp'],
                error_text='SYNTHETIC_OFFLINE: never submitted',sale_id=record['row']['record_id'])
            for code in qr:self.insert('retail_its_operation_items',operation_id=operation,qr_code=code,is_active=False)
        for record in [r for r in self.sale_rows if r['rx']][:100]:
            self.insert('prescription_sale_operations',operation_id=self.uid(),prescription_uid=record['rx']['prescription_uid'],
                sale_id=record['row']['record_id'],operation_type='QR_FINALIZE',operation_status='local_completed',
                qr_codes=[x['box']['qr'] for x in record['items']],requested_by='admin',prepared_at=record['stamp'],
                local_completed_at=record['stamp'],remote_message='Synthetic local-only finalization; not reported remotely.')
        # Each barcode aggregate is refreshed by movement triggers, including physical consignments.
        for med in self.medicines:self.db.execute('select pharmacy_refresh_consignment_quantity(%s)',(med['barcode'],))

    def loyalty(self):
        by_patient=defaultdict(list)
        for rx in self.rx_rows:by_patient[self.identity_to_id(rx['patient_tc_identity_no'])].append(rx)
        chosen=self.r.sample(list(self.patient_rows),100)
        for position,patient in enumerate(chosen,1):
            self.insert('loyal_patient_benchmark_cohort',cohort_key='LOYALTY_BENCHMARK_V1',cohort_position=position,patient_record_id=patient,selected_at=self.start)
        reports=set(self.r.sample(list(self.patient_rows),300))
        for patient,rxs in by_patient.items():
            latest=max(rx['saved_at'] for rx in rxs)
            self.insert('loyal_patient_profile',patient_record_id=patient,medication_history_synced_at=latest,report_history_synced_at=latest)
            for rx in rxs:
                for row in rx['final_content']:
                    self.insert('loyal_patient_medication_history',patient_record_id=patient,prescription_no=rx['medula_prescription_no'],
                        medication_barcode=row[0],medication_purchase_date=rx['prescription_date'],available_date=rx['prescription_date']+timedelta(days=30),
                        quantity=1,medication_usage='',synced_at=rx['saved_at'])
            if patient in reports:
                self.insert('loyal_patient_report_history',patient_record_id=patient,report_tracking_no=f'SYNREP{patient:07}',
                    report_no=f'SYNR{patient:07}',start_date=self.start.date(),end_date=self.end.date()+timedelta(days=180),
                    diagnosis='',synced_at=latest)
            loyal=len(rxs)>=2;score=80 if loyal else 25
            self.db.execute('''update patient set is_loyal=%s,is_loyal_mechanism1=%s,is_loyal_mechanism2=%s,is_loyal_mechanism3=%s,
                loyalty_mechanism3_state=%s,loyalty_mechanism3_score=%s,loyalty_mechanism3_evaluated_at=%s,
                loyalty_report_evidence_count=%s,loyalty_report_ever_qualified=%s where record_id=%s''',
                (loyal,loyal,loyal,loyal,1 if loyal else 0,score,latest,len(rxs),loyal,patient))
        # The four monthly sample runs share the same stable benchmark cohort.
        for n in range(4):
            stamp=self.start+timedelta(days=min(6+n*7,self.days-1))
            if stamp>self.end:stamp=self.end-timedelta(minutes=1)
            counts={p:sum(rx['saved_at']<=stamp for rx in by_patient[p]) for p in chosen}
            run=self.uid();good=sum(counts[p]>=2 for p in chosen)
            self.insert('loyal_patient_report_sample_run',run_id=run,started_at=stamp,completed_at=stamp,
                configured_sample_size=100,actual_sample_size=100,threshold_ratio=Decimal('.6'),delay_seconds=0,jitter_seconds=0,
                total_patient_count=1000,evaluated_patient_count=100,failed_patient_count=0,patient_with_report_count=sum(n>0 for n in counts.values()),
                loyal_sample_count=good,sample_loyal_ratio=Decimal(good)/100,estimated_loyal_patient_count=good*10,status='COMPLETED',
                evidence_source='MEDICATION_HISTORY',mechanism1_loyal_count=good,mechanism1_loyal_ratio=Decimal(good)/100,mechanism1_estimated_count=good*10,
                mechanism2_loyal_count=good,mechanism2_loyal_ratio=Decimal(good)/100,mechanism2_estimated_count=good*10,
                mechanism3_loyal_count=good,mechanism3_loyal_ratio=Decimal(good)/100,mechanism3_estimated_count=good*10)
            for patient in chosen:
                count=counts[patient];loyal=count>=2;score=80 if loyal else (25 if count else 0)
                self.insert('loyal_patient_report_supply_evidence',run_id=run,patient_record_id=patient,
                    patient_tc_identity_no=self.patient_rows[patient]['tc_identity_no'],total_report_count=count,supplied_report_count=count,
                    supply_ratio=1 if count else None,is_loyal=loyal,evaluated_at=stamp,has_report_evidence=count>0,is_report_decisive=False,
                    frequency_score=score,report_score=0,family_score=0,hybrid_score=score,mechanism3_is_loyal=loyal,
                    loyal_family_member_count=0,explicitly_unloyal_family_member_count=0,decision_reason='',
                    evidence_source='MEDICATION_HISTORY',mechanism1_is_loyal=loyal,mechanism2_is_loyal=loyal)

    def identity_to_id(self, identity):
        return next(p for p,row in self.patient_rows.items() if row['tc_identity_no']==identity)

    def sync_records(self):
        run=self.uid()
        self.insert('invoice_incremental_sync_state',source_key='SYNTHETIC_OFFLINE',last_successful_cutoff=self.end,
            active_run_id=run,phase='Completed',total=100,completed=100,imported=100,failed=0,updated_at=self.end)
        for row in self.invoice_rows:
            self.insert('invoice_incremental_sync_items',run_id=run,type_key='Gelen Fatura',document_uuid=row['id'],
                document_number='SYN-'+row['id'][:8],issue_date=row['stamp'].date(),received_at=row['stamp'],status='Imported',updated_at=row['stamp'])
        for table in self.audit_tables:
            # Keep each entity's audit sequence ordered within the generation window.
            self.db.execute(sql.SQL('''WITH ordered AS (
                select log_id,row_number() over(partition by record_key order by log_id) as n,
                count(*) over(partition by record_key) as total from {})
                update {} t set timestamp=%s::timestamptz + ((%s::timestamptz-%s::timestamptz)*
                    ((o.n-1)::double precision/greatest(o.total,1))) from ordered o where t.log_id=o.log_id''').format(
                    sql.Identifier(table),sql.Identifier(table)),(self.start,self.end,self.start))

    def run(self):
        self.bootstrap();self.buy_stock();self.schedule()
        for index,(stamp,kind,patient) in enumerate(self.events):
            if kind in ['prescription','retail']:self.sale(stamp,patient,kind=='prescription')
            else:getattr(self,kind)(stamp,patient)
            if index and index%1000==0:print(f'Created {index} linked business events.',flush=True)
        self.finish_sales();self.loyalty();self.sync_records()
        self.reset_sequences()
        verify(self.db,self.days)

    def reset_sequences(self):
        rows=self.db.execute('''select table_name,column_name,pg_get_serial_sequence(quote_ident(table_schema)||'.'||quote_ident(table_name),column_name)
            from information_schema.columns where table_schema='public' and (column_default like 'nextval%%' or is_identity='YES')''').fetchall()
        for table,column,sequence in rows:
            if sequence:
                value=self.db.execute(sql.SQL('select max({}) from {}').format(sql.Identifier(column),sql.Identifier(table))).fetchone()[0]
                self.db.execute('select setval(%s::regclass,%s,%s)',(sequence,max(value or 1,1),value is not None))


def verify(db,days=30):
    checks={
        'patient count': 'select count(*)<>1000 from patient',
        'missing patient identities': 'select count(*) from patient p left join patient_identity i on i.patient_record_id=p.record_id where i.tc_identity_no is null',
        'patients without prescriptions': 'select count(*) from patient p where not exists(select 1 from prescription r where r.patient_tc_identity_no=p.tc_identity_no)',
        'incorrect patient prescription counts': '''select count(*) from patient p where prescription_count<>(select count(*) from prescription r join patient_identity i on i.tc_identity_no=r.patient_tc_identity_no where i.patient_record_id=p.record_id and not r.is_deleted)''',
        'sale line totals': '''select count(*) from sales s where total_amount<>(select coalesce(sum(exit_price),0) from "salesQrItems" i where i.sale_id=s.record_id)''',
        'sale allocations': '''select count(*) from sales where total_amount<>patient_amount+institution_amount+institution_secondary_amount''',
        'prescription sale links': '''select count(*) from sales s left join prescription r on r.prescription_uid=s.prescription_uid left join patient p on p.record_id=s.patient_id where s.prescription_uid is not null and (r.record_id is null or r.patient_tc_identity_no<>p.tc_identity_no or r.prescription_total<>s.total_amount or r.patient_total_payment<>s.patient_amount or r.institution_total_payment<>s.institution_amount)''',
        'sale payment allocations': '''select count(*) from sales s where total_amount<>(select coalesce(sum(amount),0) from "salesPayements" p where p.sale_id=s.record_id)''',
        'refund totals': '''select count(*) from "salesRefunds" r where total_amount<>(select coalesce(sum(exit_price),0) from "salesRefundItems" i where i.refund_id=r.record_id)''',
        'patient debt': '''select count(*) from patient p where debt_amount<>(select coalesce(sum(case when debt_direction='+' then debt_change else -debt_change end),0) from patient_debt_log d where d.patient_record_id=p.record_id)''',
        'depot debt': '''select count(*) from depot d where debt_balance<>(select coalesce(sum(i.payable_amount),0) from invoices i join addresses a on a.record_id=i.sender_address_id where a.gln=d.gln_code)''',
        'pharmacy debt': '''select count(*) from pharmacy p where debt_balance<>(select coalesce(sum(q.debt_amount),0) from "takasQrItems" q join takas t on t.record_id=q.takas_id where t.pharmacy_id=p.record_id and q.status='accepted')+(select coalesce(sum(b.debt_amount),0) from "takasBarcodeItems" b join takas t on t.record_id=b.takas_id where t.pharmacy_id=p.record_id and b.status='accepted')''',
        'trade item totals': '''select count(*) from takas t where debt_amount<>(select coalesce(sum(debt_amount),0) from "takasQrItems" where takas_id=t.record_id)+(select coalesce(sum(debt_amount),0) from "takasBarcodeItems" where takas_id=t.record_id)''',
        'invoice totals': '''select count(*) from invoices i where total_cost<>(select coalesce(sum(line_amount),0) from invoice_lines l where l.invoice_uuid=i.invoice_uuid) or payable_amount<>total_cost-discount_amount+kdv_amount''',
        'invoice QR quantities': '''select count(*) from invoice_lines l where stock_tracking_type='QR' and quantity<>(select count(*) from invoice_line_qr_codes q where q.invoice_line_id=l.record_id)''',
        'QR overlap': '''select count(*) from "qrActive" a join "qrInactive" i using(qr_code)''',
        'QR hanging overlap': '''select count(*) from "qrHanged" h where exists(select 1 from "qrActive" a where a.qr_code=h.qr_code) or exists(select 1 from "qrInactive" i where i.qr_code=h.qr_code)''',
        'aggregate QR stock': '''select count(*) from "barcodeActive" b where quantity<>(select count(*) from "qrActive" q where q.barcode=b.barcode)''',
        'consignment quantities': '''select count(*) from "barcodeActive" b where consignment_quantity<>(select count(*) from "qrActive" q where q.barcode=b.barcode and upper(coalesce(q.status,'')) in ('RESERVED','EMANET'))''',
        'reservation alignment': '''select count(*) from "consignmentSaleItems" i join "consignmentSales" s on s.record_id=i.consignment_sale_id left join "qrActive" q on q.qr_code=i.qr_code where s.status='reserved' and (q.qr_code is null or q.status<>'RESERVED')''',
        'family membership': '''select count(*) from family_group g where member_ids<>(select coalesce(array_agg(record_id order by record_id),array[]::integer[]) from patient p where p.family_group_id=g.record_id) or not(g.main_patient_id=any(g.member_ids))''',
        'refund source links': '''select count(*) from "salesRefundItems" r where not exists(select 1 from "salesQrItems" s where s.sale_id=r.original_sale_id and s.qr_code=r.qr_code and s.barcode=r.barcode and s.exit_price=r.exit_price)''',
        'loyalty chronology': '''select count(*) from loyal_patient_report_supply_evidence e where total_report_count<>(select count(*) from prescription p join patient_identity i on i.tc_identity_no=p.patient_tc_identity_no where i.patient_record_id=e.patient_record_id and p.saved_at<=e.evaluated_at)''',
        'sample summary counts': '''select count(*) from loyal_patient_report_sample_run r where actual_sample_size<>(select count(*) from loyal_patient_report_supply_evidence e where e.run_id=r.run_id) or loyal_sample_count<>(select count(*) from loyal_patient_report_supply_evidence e where e.run_id=r.run_id and e.is_loyal)''',
        'finalized prescription QR codes': '''select count(*) from prescription p where p.qr_status_code<>2 or exists(select 1 from unnest(p.qr_codes) code where not exists(select 1 from "qrInactive" i where i.qr_code=code))''',
        'card enum values': '''select (select count(*) from patient where gender not in ('Erkek','Kadın','Belirtilmemiş'))+(select count(*) from faculty where ownership_type not in ('Kamu','Özel'))+(select count(*) from users where user_id like 'demo%' and permisson_level not in ('Eleman','Eczacı','A'))''',
        'shipment chronology': '''select count(*) from "salesQrItems" s join sales h on h.record_id=s.sale_id where not exists(select 1 from "qrMovements" m where m.qr_code=s.qr_code and m.movement_type=1 and m.movement_date<=h.timestamp)''',
        'unsafe service queues': '''select (select count(*) from retail_its_operations where operation_status<>'failed')+(select count(*) from stock_its_operations where operation_status<>'failed')''',
    }
    for label,query in checks.items():
        value=db.execute(query).fetchone()[0]
        if value:raise AssertionError(f'{label}: {value} inconsistencies')
    if db.execute("select count(*)<>1 or not bool_and(user_id='admin') from users").fetchone()[0]:
        raise AssertionError('Only the administrator account may exist.')
    for table in ['doctor','depot','pharmacy','faculty','family_group']:
        if db.execute(sql.SQL('select count(*) from {}').format(sql.Identifier(table))).fetchone()[0]!=100:
            raise AssertionError('Expected 100 synthetic rows: '+table)
    end=db.execute("select last_successful_cutoff from invoice_incremental_sync_state where source_key='SYNTHETIC_OFFLINE'").fetchone()[0].astimezone(ZoneInfo('Europe/Istanbul'))
    start=datetime.combine(end.date()-timedelta(days=days-1),time(0),end.tzinfo)
    for table,column,aware in [('sales','timestamp',False),('prescription','saved_at',True),('qrMovements','movement_date',False),
        ('invoices','database_entry_at',True),('salesRefunds','timestamp',False),('takas','created_at',False),
        ('consignmentSales','timestamp',False),('manual_movements','timestamp',False),('patient_debt_log','timestamp',True)]:
        bounds=(start,end) if aware else (start.replace(tzinfo=None),end.replace(tzinfo=None))
        query=sql.SQL('select count(*) from {} where {}<%s or {}>%s').format(sql.Identifier(table),sql.Identifier(column),sql.Identifier(column))
        if db.execute(query,bounds).fetchone()[0]:raise AssertionError('Activity outside the generation window: '+table)
    # Discover and check every declared FK independently of insertion order/constraint timing.
    fks=db.execute('''select c.conrelid::regclass::text,c.confrelid::regclass::text,
        array(select attname from unnest(c.conkey) with ordinality k(n,o) join pg_attribute a on a.attrelid=c.conrelid and a.attnum=k.n order by o),
        array(select attname from unnest(c.confkey) with ordinality k(n,o) join pg_attribute a on a.attrelid=c.confrelid and a.attnum=k.n order by o)
        from pg_constraint c where c.contype='f' and c.connamespace='public'::regnamespace''').fetchall()
    for child,parent,cols,refs in fks:
        child=child.strip('"');parent=parent.strip('"')
        join=sql.SQL(' AND ').join(sql.SQL('c.{}=p.{}').format(sql.Identifier(a),sql.Identifier(b)) for a,b in zip(cols,refs))
        nonnull=sql.SQL(' AND ').join(sql.SQL('c.{} IS NOT NULL').format(sql.Identifier(a)) for a in cols)
        query=sql.SQL('select count(*) from {} c where {} and not exists(select 1 from {} p where {})').format(sql.Identifier(child),nonnull,sql.Identifier(parent),join)
        if db.execute(query).fetchone()[0]:raise AssertionError('Foreign key mismatch: '+child+' -> '+parent)
    counts={name:db.execute(sql.SQL('select count(*) from {}').format(sql.Identifier(name))).fetchone()[0]
            for name, in db.execute("select tablename from pg_tables where schemaname='public' order by 1").fetchall()}
    if any(count==0 for count in counts.values()):
        raise AssertionError('Unpopulated tables: '+','.join(n for n,v in counts.items() if not v))
    print(json.dumps({'checks_passed':len(checks)+len(fks),'row_counts':counts},indent=2),flush=True)


def main():
    parser=argparse.ArgumentParser()
    parser.add_argument('--config',type=Path,default=Path(__file__).resolve().parents[3]/'config.json')
    parser.add_argument('--seed',type=int,default=20261026)
    parser.add_argument('--days',type=int,default=30)
    parser.add_argument('--dry-run',action='store_true')
    parser.add_argument('--verify-only',action='store_true')
    args=parser.parse_args()
    cfg=json5.loads(args.config.read_text(encoding='utf-8-sig'))['Database']
    with psycopg.connect(host=cfg['Hostname'],port=cfg['Port'],dbname=cfg['DatabaseName'],user=cfg['Username'],password=cfg['Password'],connect_timeout=8) as db:
        if db.execute('select current_database(),current_user').fetchone()!=('Portfolio','portfolio'):
            raise RuntimeError('Only the dedicated synthetic database/login may be used.')
        if args.verify_only:verify(db,args.days)
        else:
            if args.days<8:raise ValueError('The linked scenario requires at least eight days.')
            Generator(db,args.seed,args.days,datetime.now(ZoneInfo('Europe/Istanbul'))).run()
            if args.dry_run:
                db.rollback();print('Dry run passed; generated rows rolled back.',flush=True)
            else:print('Validation passed; committing synthetic data.',flush=True)


if __name__=='__main__':
    main()
