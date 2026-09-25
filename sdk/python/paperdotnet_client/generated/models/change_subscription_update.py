from __future__ import annotations
import datetime
from collections.abc import Callable
from dataclasses import dataclass, field
from kiota_abstractions.serialization import AdditionalDataHolder, Parsable, ParseNode, SerializationWriter
from typing import Any, Optional, TYPE_CHECKING, Union

@dataclass
class ChangeSubscriptionUpdate(AdditionalDataHolder, Parsable):
    """
    Renewal body: a new expiry and optionally a new client state.
    """
    # Stores additional data not described in the OpenAPI description found when deserializing. Can be used for serialization as well.
    additional_data: dict[str, Any] = field(default_factory=dict)

    # The clientState property
    client_state: Optional[str] = None
    # The expirationDateTime property
    expiration_date_time: Optional[datetime.datetime] = None
    
    @staticmethod
    def create_from_discriminator_value(parse_node: ParseNode) -> ChangeSubscriptionUpdate:
        """
        Creates a new instance of the appropriate class based on discriminator value
        param parse_node: The parse node to use to read the discriminator value and create the object
        Returns: ChangeSubscriptionUpdate
        """
        if parse_node is None:
            raise TypeError("parse_node cannot be null.")
        return ChangeSubscriptionUpdate()
    
    def get_field_deserializers(self,) -> dict[str, Callable[[ParseNode], None]]:
        """
        The deserialization information for the current model
        Returns: dict[str, Callable[[ParseNode], None]]
        """
        fields: dict[str, Callable[[Any], None]] = {
            "clientState": lambda n : setattr(self, 'client_state', n.get_str_value()),
            "expirationDateTime": lambda n : setattr(self, 'expiration_date_time', n.get_datetime_value()),
        }
        return fields
    
    def serialize(self,writer: SerializationWriter) -> None:
        """
        Serializes information the current object
        param writer: Serialization writer to use to serialize this model
        Returns: None
        """
        if writer is None:
            raise TypeError("writer cannot be null.")
        writer.write_str_value("clientState", self.client_state)
        writer.write_datetime_value("expirationDateTime", self.expiration_date_time)
        writer.write_additional_data_value(self.additional_data)
    

