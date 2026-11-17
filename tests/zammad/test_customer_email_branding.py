"""Owner, 18 November 2026: customer emails from the helpdesk carry no Zammad branding or Zammad login link."""
import pathlib
import re
import unittest

PROVISION = pathlib.Path(__file__).resolve().parents[2] / 'containers/zammad/provision.rb'
STOCK = ('auto reply (on new tickets)', 'auto reply (on follow-up of tickets)', 'customer notification (on owner change)')


class CustomerEmailBranding(unittest.TestCase):
    def setUp(self):
        text = PROVISION.read_text(encoding='utf-8')
        start = text.index('# Owner, 18 November 2026: emails to customers')
        self.block = text[start:text.index('# Webhook/trigger stay inactive', start)]
        self.bodies = dict(re.findall(r"'([^']+)' =>\s*'([^']*)'", self.block))

    def test_every_stock_customer_email_is_reworded(self):
        self.assertEqual(set(self.bodies), set(STOCK))

    def test_reworded_emails_have_no_zammad_branding_or_login_link(self):
        for name, body in self.bodies.items():
            with self.subTest(name):
                self.assertNotRegex(body, r'(?i)zammad|customer support system|product_name|config\.fqdn|ticket/zoom|password')
                self.assertIn('Swoovr Support', body)
                self.assertIn('reply to this email', body)

    def test_only_stock_text_is_replaced(self):
        self.assertIn("match?(/customer support system|config\\.product_name/)", self.block)


if __name__ == '__main__':
    unittest.main()
