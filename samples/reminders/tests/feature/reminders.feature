Feature: Reminders

  Scenario: A customer whose invoice is overdue is reminded
    Given billing holds an unpaid invoice of 120 for order 7, due yesterday
    And billing accepts reminders
    When the reminders run
    Then billing is asked to remind the customer of 120 for order 7
    And the reminder of 120 for order 7 is announced

  Scenario: An invoice not yet due is not reminded of
    Given billing holds an unpaid invoice of 120 for order 7, due tomorrow
    When the reminders run
    Then billing is asked to remind nobody

  Scenario: The scheduler reminds as many customers a run as it is set to
    Given billing holds unpaid invoices of 120 for order 7 and 80 for order 9, both due yesterday
    And billing accepts reminders
    When the scheduler fires the reminders
    Then billing is asked to remind one customer

  Scenario: The reminders are run by hand
    Given billing holds an unpaid invoice of 80 for order 9, due yesterday
    And billing accepts reminders
    When someone runs the reminders by hand
    Then billing is asked to remind the customer of 80 for order 9

  # Billing may list an invoice as unpaid a while after it reports the invoice paid.
  Scenario: An invoice billing has reported paid is not reminded of while billing still lists it
    Given billing holds an unpaid invoice of 120 for order 7, due yesterday
    And billing has reported order 7 paid
    When the reminders run
    Then billing is asked to remind nobody

  Scenario: A payment billing reports is acknowledged
    When billing reports order 7 paid
    Then the report is acknowledged

  Scenario: A payment report the reminders cannot read is dropped
    When billing reports a payment nobody can read
    Then the report is dropped
