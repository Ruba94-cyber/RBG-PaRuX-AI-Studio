#include <QObject>

class Worker : public QObject {
    Q_OBJECT
public:
    void attach(QObject* source) {
        QObject::connect(source, SIGNAL(done()), this, SLOT(onDone()));
    }
public slots:
    void onDone() {}
};
