# RabbitMQ with the delayed-message-exchange plugin.
# The escrow saga schedules its timeouts (payment window, provider response, inspection window)
# as delayed messages, and MassTransit's delayed scheduler needs this plugin on RabbitMQ.
FROM rabbitmq:3.13-management-alpine

ADD https://github.com/rabbitmq/rabbitmq-delayed-message-exchange/releases/download/v3.13.0/rabbitmq_delayed_message_exchange-3.13.0.ez /opt/rabbitmq/plugins/

RUN chown rabbitmq:rabbitmq /opt/rabbitmq/plugins/rabbitmq_delayed_message_exchange-3.13.0.ez \
 && chmod 644 /opt/rabbitmq/plugins/rabbitmq_delayed_message_exchange-3.13.0.ez \
 && rabbitmq-plugins enable --offline rabbitmq_delayed_message_exchange
